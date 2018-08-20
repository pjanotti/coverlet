using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Reflection;

using Coverlet.Core.Attributes;
using Coverlet.Core.Helpers;
using Coverlet.Core.Symbols;

using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

namespace Coverlet.Core.Instrumentation
{
    internal class Instrumenter
    {
        private readonly string _module;
        private readonly string _identifier;
        private readonly string[] _excludeFilters;
        private readonly string[] _includeFilters;
        private readonly string[] _excludedFiles;
        private InstrumenterResult _result;
        private FieldDefinition _customModuleTrackerHitsArray;
        private FieldDefinition _customModuleTrackerHitsFilePath;
        private ILProcessor _customModuleTrackerClassConstructorIl;
        private MethodReference _cachedInterlockedIncMethod;

        public Instrumenter(string module, string identifier, string[] excludeFilters, string[] includeFilters, string[] excludedFiles)
        {
            _module = module;
            _identifier = identifier;
            _excludeFilters = excludeFilters;
            _includeFilters = includeFilters;
            _excludedFiles = excludedFiles ?? Array.Empty<string>();
        }

        public bool CanInstrument() => InstrumentationHelper.HasPdb(_module);

        public InstrumenterResult Instrument()
        {
            string hitsFilePath = Path.Combine(
                Path.GetTempPath(),
                Path.GetFileNameWithoutExtension(_module) + "_" + _identifier
            );

            _result = new InstrumenterResult
            {
                Module = Path.GetFileNameWithoutExtension(_module),
                HitsFilePath = hitsFilePath,
                ModulePath = _module
            };

            InstrumentModule();

            return _result;
        }

        private void InstrumentModule()
        {
            using (var stream = new FileStream(_module, FileMode.Open, FileAccess.ReadWrite))
            using (var resolver = new DefaultAssemblyResolver())
            {
                resolver.AddSearchDirectory(Path.GetDirectoryName(_module));
                var parameters = new ReaderParameters { ReadSymbols = true, AssemblyResolver = resolver };

                using (var module = ModuleDefinition.ReadModule(stream, parameters))
                {
                    var types = module.GetTypes();
                    AddCustomModuleTrackerToModule(module);

                    foreach (TypeDefinition type in types)
                    {
                        var actualType = type.DeclaringType ?? type;
                        if (!actualType.CustomAttributes.Any(IsExcludeAttribute)
                            && actualType.Namespace != "Coverlet.Core.Instrumentation.Tracker"
                            && !InstrumentationHelper.IsTypeExcluded(_module, actualType.FullName, _excludeFilters)
                            && InstrumentationHelper.IsTypeIncluded(_module, actualType.FullName, _includeFilters))
                            InstrumentType(type);
                    }

                    // Fixup the custom tracker class constructor, according to all instrumented types
                    Instruction lastInstr = _customModuleTrackerClassConstructorIl.Body.Instructions.Last();
                    _customModuleTrackerClassConstructorIl.InsertBefore(lastInstr, Instruction.Create(OpCodes.Ldc_I4, _result.HitCandidates.Count));
                    _customModuleTrackerClassConstructorIl.InsertBefore(lastInstr, Instruction.Create(OpCodes.Newarr, module.TypeSystem.Int32));
                    _customModuleTrackerClassConstructorIl.InsertBefore(lastInstr, Instruction.Create(OpCodes.Stsfld, _customModuleTrackerHitsArray));
                    _customModuleTrackerClassConstructorIl.InsertBefore(lastInstr, Instruction.Create(OpCodes.Ldstr, _result.HitsFilePath));
                    _customModuleTrackerClassConstructorIl.InsertBefore(lastInstr, Instruction.Create(OpCodes.Stsfld, _customModuleTrackerHitsFilePath));

                    module.Write(stream);
                }
            }
        }

        private void AddCustomModuleTrackerToModule(ModuleDefinition module)
        {
            using (AssemblyDefinition xad = AssemblyDefinition.ReadAssembly(typeof(Instrumenter).Assembly.Location))
            {
                TypeDefinition moduleTrackerTemplate = xad.MainModule.GetType(
                    "Coverlet.Core.Instrumentation", nameof(ModuleTrackerTemplate));

                TypeDefinition customTrackerTypeDef = new TypeDefinition(
                    "Coverlet.Core.Instrumentation.Tracker", Path.GetFileNameWithoutExtension(module.Name) + "_" + _identifier, moduleTrackerTemplate.Attributes);

                customTrackerTypeDef.BaseType = module.TypeSystem.Object;
                foreach (FieldDefinition fieldDef in moduleTrackerTemplate.Fields)
                {
                    var fieldClone = new FieldDefinition(fieldDef.Name, fieldDef.Attributes, fieldDef.FieldType);
                    customTrackerTypeDef.Fields.Add(fieldClone);

                    if (fieldClone.Name == "HitsArray")
                        _customModuleTrackerHitsArray = fieldClone;
                    else if (fieldClone.Name == "HitsFilePath")
                        _customModuleTrackerHitsFilePath = fieldClone;
                }

                foreach (MethodDefinition methodDef in moduleTrackerTemplate.Methods)
                {
                    MethodDefinition methodOnCustomType = new MethodDefinition(methodDef.Name, methodDef.Attributes, methodDef.ReturnType);

                    foreach (var variable in methodDef.Body.Variables)
                    {
                        methodOnCustomType.Body.Variables.Add(new VariableDefinition(module.ImportReference(variable.VariableType)));
                    }

                    methodOnCustomType.Body.InitLocals = methodDef.Body.InitLocals;

                    ILProcessor ilProcessor = methodOnCustomType.Body.GetILProcessor();
                    if (methodDef.Name == ".cctor")
                        _customModuleTrackerClassConstructorIl = ilProcessor;

                    foreach (Instruction instr in methodDef.Body.Instructions)
                    {
                        if (instr.Operand is MethodReference methodReference)
                        {
                            if (!methodReference.FullName.Contains(moduleTrackerTemplate.Namespace))
                            {
                                // External method references, just import then
                                instr.Operand = module.ImportReference(methodReference);
                            }
                            else
                            {
                                // Move to the custom type
                                instr.Operand = new MethodReference(
                                    methodReference.Name, methodReference.ReturnType, customTrackerTypeDef);
                            }
                        }
                        else if (instr.Operand is FieldReference fieldReference)
                        {
                            instr.Operand = customTrackerTypeDef.Fields.Single(fd => fd.Name == fieldReference.Name);
                        }
                        else if (instr.Operand is TypeReference typeReference)
                        {
                            instr.Operand = module.ImportReference(typeReference);
                        }

                        ilProcessor.Append(instr);
                    }

                    foreach (var handler in methodDef.Body.ExceptionHandlers)
                        methodOnCustomType.Body.ExceptionHandlers.Add(handler);

                    customTrackerTypeDef.Methods.Add(methodOnCustomType);
                }

                module.Types.Add(customTrackerTypeDef);
            }

            Debug.Assert(_customModuleTrackerHitsArray != null);
            Debug.Assert(_customModuleTrackerClassConstructorIl != null);
        }

        private void InstrumentType(TypeDefinition type)
        {
            var methods = type.GetMethods();
            foreach (var method in methods)
            {
                MethodDefinition actualMethod = method;
                if (InstrumentationHelper.IsLocalMethod(method.Name))
                    actualMethod = methods.FirstOrDefault(m => m.Name == method.Name.Split('>')[0].Substring(1)) ?? method;

                if (!actualMethod.CustomAttributes.Any(IsExcludeAttribute))
                    InstrumentMethod(method);
            }

            var ctors = type.GetConstructors();
            foreach (var ctor in ctors)
            {
                if (!ctor.CustomAttributes.Any(IsExcludeAttribute))
                    InstrumentMethod(ctor);
            }
        }

        private void InstrumentMethod(MethodDefinition method)
        {
            var sourceFile = method.DebugInformation.SequencePoints.Select(s => s.Document.Url).FirstOrDefault();
            if (!string.IsNullOrEmpty(sourceFile) && _excludedFiles.Contains(sourceFile))
                return;

            var methodBody = GetMethodBody(method);
            if (methodBody == null)
                return;

            if (method.IsNative)
                return;

            InstrumentIL(method);
        }

        private void InstrumentIL(MethodDefinition method)
        {
            method.Body.SimplifyMacros();
            ILProcessor processor = method.Body.GetILProcessor();

            var index = 0;
            var count = processor.Body.Instructions.Count;

            var branchPoints = CecilSymbolHelper.GetBranchPoints(method);

            for (int n = 0; n < count; n++)
            {
                var instruction = processor.Body.Instructions[index];
                var sequencePoint = method.DebugInformation.GetSequencePoint(instruction);
                var targetedBranchPoints = branchPoints.Where(p => p.EndOffset == instruction.Offset);

                if (sequencePoint != null && !sequencePoint.IsHidden)
                {
                    var target = AddInstrumentationCode(method, processor, instruction, sequencePoint);
                    foreach (var _instruction in processor.Body.Instructions)
                        ReplaceInstructionTarget(_instruction, instruction, target);

                    foreach (ExceptionHandler handler in processor.Body.ExceptionHandlers)
                        ReplaceExceptionHandlerBoundary(handler, instruction, target);

                    index += 5;
                }

                foreach (var _branchTarget in targetedBranchPoints)
                {
                    /*
                        * Skip branches with no sequence point reference for now.
                        * In this case for an anonymous class the compiler will dynamically create an Equals 'utility' method.
                        * The CecilSymbolHelper will create branch points with a start line of -1 and no document, which
                        * I am currently not sure how to handle.
                        */
                    if (_branchTarget.StartLine == -1 || _branchTarget.Document == null)
                        continue;

                    var target = AddInstrumentationCode(method, processor, instruction, _branchTarget);
                    foreach (var _instruction in processor.Body.Instructions)
                        ReplaceInstructionTarget(_instruction, instruction, target);

                    foreach (ExceptionHandler handler in processor.Body.ExceptionHandlers)
                        ReplaceExceptionHandlerBoundary(handler, instruction, target);

                    index += 5;
                }

                index++;
            }

            method.Body.OptimizeMacros();
        }

        private Instruction AddInstrumentationCode(MethodDefinition method, ILProcessor processor, Instruction instruction, SequencePoint sequencePoint)
        {
            if (!_result.Documents.TryGetValue(sequencePoint.Document.Url, out var document))
            {
                document = new Document { Path = sequencePoint.Document.Url };
                document.Index = _result.Documents.Count;
                _result.Documents.Add(document.Path, document);
            }

            for (int i = sequencePoint.StartLine; i <= sequencePoint.EndLine; i++)
            {
                if (!document.Lines.ContainsKey(i))
                    document.Lines.Add(i, new Line { Number = i, Class = method.DeclaringType.FullName, Method = method.FullName });
            }

            // string marker = $"L,{document.Index},{sequencePoint.StartLine},{sequencePoint.EndLine}";
            var entry = (false, document.Index, sequencePoint.StartLine, sequencePoint.EndLine);
            _result.HitCandidates.Add(entry);

            return AddInstrumentationInstructions(method, processor, instruction, _result.HitCandidates.Count);
        }

        private Instruction AddInstrumentationCode(MethodDefinition method, ILProcessor processor, Instruction instruction, BranchPoint branchPoint)
        {
            if (!_result.Documents.TryGetValue(branchPoint.Document, out var document))
            {
                document = new Document { Path = branchPoint.Document };
                document.Index = _result.Documents.Count;
                _result.Documents.Add(document.Path, document);
            }

            var key = (branchPoint.StartLine, (int)branchPoint.Ordinal);
            if (!document.Branches.ContainsKey(key))
                document.Branches.Add(key,
                    new Branch
                    {
                        Number = branchPoint.StartLine,
                        Class = method.DeclaringType.FullName,
                        Method = method.FullName,
                        Offset = branchPoint.Offset,
                        EndOffset = branchPoint.EndOffset,
                        Path = branchPoint.Path,
                        Ordinal = branchPoint.Ordinal
                    }
                );

            // string marker = $"B,{document.Index},{branchPoint.StartLine},{branchPoint.Ordinal}";
            var entry = (true, document.Index, branchPoint.StartLine, (int)branchPoint.Ordinal);
            _result.HitCandidates.Add(entry);

            return AddInstrumentationInstructions(method, processor, instruction, _result.HitCandidates.Count);
        }

        private Instruction AddInstrumentationInstructions(MethodDefinition method, ILProcessor processor, Instruction instruction, int hitEntryIndex)
        {
            if (_cachedInterlockedIncMethod == null)
            {
                _cachedInterlockedIncMethod = new MethodReference(
                    "Increment", method.Module.TypeSystem.Int32, method.Module.ImportReference(typeof(System.Threading.Interlocked)));
                _cachedInterlockedIncMethod.Parameters.Add(new ParameterDefinition(new ByReferenceType(method.Module.TypeSystem.Int32)));
            }

            var sfldInstr = Instruction.Create(OpCodes.Ldsfld, _customModuleTrackerHitsArray);
            var indxInstr = Instruction.Create(OpCodes.Ldc_I4, hitEntryIndex);
            var arefInstr = Instruction.Create(OpCodes.Ldelema, method.Module.TypeSystem.Int32);
            var callInstr = Instruction.Create(OpCodes.Call, _cachedInterlockedIncMethod);
            var popInstr = Instruction.Create(OpCodes.Pop);

            processor.InsertBefore(instruction, popInstr);
            processor.InsertBefore(popInstr, callInstr);
            processor.InsertBefore(callInstr, arefInstr);
            processor.InsertBefore(arefInstr, indxInstr);
            processor.InsertBefore(indxInstr, sfldInstr);

            return sfldInstr;
        }

        private static void ReplaceInstructionTarget(Instruction instruction, Instruction oldTarget, Instruction newTarget)
        {
            if (instruction.Operand is Instruction _instruction)
            {
                if (_instruction == oldTarget)
                {
                    instruction.Operand = newTarget;
                    return;
                }
            }
            else if (instruction.Operand is Instruction[] _instructions)
            {
                for (int i = 0; i < _instructions.Length; i++)
                {
                    if (_instructions[i] == oldTarget)
                        _instructions[i] = newTarget;
                }
            }
        }

        private static void ReplaceExceptionHandlerBoundary(ExceptionHandler handler, Instruction oldTarget, Instruction newTarget)
        {
            if (handler.FilterStart == oldTarget)
                handler.FilterStart = newTarget;

            if (handler.HandlerEnd == oldTarget)
                handler.HandlerEnd = newTarget;

            if (handler.HandlerStart == oldTarget)
                handler.HandlerStart = newTarget;

            if (handler.TryEnd == oldTarget)
                handler.TryEnd = newTarget;

            if (handler.TryStart == oldTarget)
                handler.TryStart = newTarget;
        }

        private static bool IsExcludeAttribute(CustomAttribute customAttribute)
        {
            var excludeAttributeNames = new[]
            {
                nameof(ExcludeFromCoverageAttribute),
                "ExcludeFromCoverage",
                nameof(ExcludeFromCodeCoverageAttribute),
                "ExcludeFromCodeCoverage"
            };

            var attributeName = customAttribute.AttributeType.Name;
            return excludeAttributeNames.Any(a => a.Equals(attributeName));
        }

        private static Mono.Cecil.Cil.MethodBody GetMethodBody(MethodDefinition method)
        {
            try
            {
                return method.HasBody ? method.Body : null;
            }
            catch
            {
                return null;
            }
        }
    }
}