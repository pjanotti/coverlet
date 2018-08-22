﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Coverlet.Core.Instrumentation
{
    /// <summary>
    /// This static class will be injected on a module being instrumented in order to direct on module hits
    /// to a single location.
    /// </summary>
    /// <remarks>
    /// As this type is going to be customized for each instrumeted module it doesn't follow typical practices
    /// regarding visibility of members, etc.
    /// </remarks>
    public static class ModuleTrackerTemplate
    {
        public static string HitsFilePath;
        public static int[] HitsArray;

        [ThreadStatic]
        private static int[] t_threadHits;

        private static List<int[]> _threads;

        static ModuleTrackerTemplate()
        {
            _threads = new List<int[]>(2 * Environment.ProcessorCount);

            AppDomain.CurrentDomain.ProcessExit += new EventHandler(UnloadModule);
            AppDomain.CurrentDomain.DomainUnload += new EventHandler(UnloadModule);
            // At the end of the instrumentation of a module, the instrumenter needs to add code here
            // to initialize the static fields according to the values derived from the instrumentation of
            // the module.
        }

        public static void RecordHit(int hitLocationIndex)
        {
            if (t_threadHits == null)
            {
                lock (_threads)
                {
                    if (t_threadHits == null)
                    {
                        t_threadHits = new int[HitsArray.Length];
                        _threads.Add(t_threadHits);
                    }
                }
            }

            ++t_threadHits[hitLocationIndex];
        }

        public static void UnloadModule(object sender, EventArgs e)
        {
            // Update the global hits array from data from all the threads
            lock (_threads)
            {
                foreach (var threadHits in _threads)
                {
                    for (int i = 0; i < HitsArray.Length; ++i)
                        HitsArray[i] += threadHits[i];
                }

                // Prevent any double counting scenario, i.e.: UnloadModule called twice (not sure if this can happen in practice ...)
                // Only an issue if DomainUnload and ProcessExit can both happens, perhaps can be removed...
                _threads.Clear();
            }

            // The same module can be unloaded multiple times in the same process via different app domains.
            // Use a global mutex to ensure no concurrent access.
            using (var mutex = new Mutex(true, Path.GetFileNameWithoutExtension(HitsFilePath) + "_Mutex", out bool createdNew))
            {
                if (!createdNew)
                    mutex.WaitOne();

                if (!File.Exists(HitsFilePath))
                {
                    // File not created yet, just write it
                    using (var fs = new FileStream(HitsFilePath, FileMode.Create))
                    using (var bw = new BinaryWriter(fs))
                    {
                        bw.Write(HitsArray.Length);
                        foreach (int hitCount in HitsArray)
                        {
                            bw.Write(hitCount);
                        }
                    }
                }
                else
                {
                    // Update the number of hits by adding value on disk with the ones on memory.
                    // This path should be triggered only in the case of multiple AppDomain unloads.
                    using (var fs = File.Open(HitsFilePath, FileMode.Open))
                    using (var br = new BinaryReader(fs))
                    using (var bw = new BinaryWriter(fs))
                    {
                        int hitsLength = br.ReadInt32();
                        if (hitsLength != HitsArray.Length)
                        {
                            throw new InvalidDataException(
                                $"{HitsFilePath} has {hitsLength} entries but on memory {nameof(HitsArray)} has {HitsArray.Length}");
                        }

                        for (int i = 0; i < hitsLength; ++i)
                        {
                            int oldHitCount = br.ReadInt32();
                            bw.Seek(-sizeof(int), SeekOrigin.Current);
                            bw.Write(HitsArray[i] + oldHitCount);
                        }
                    }
                }

                // Prevent any double counting scenario, i.e.: UnloadModule called twice (not sure if this can happen in practice ...)
                // Only an issue if DomainUnload and ProcessExit can both happens, perhaps can be removed...
                Array.Clear(HitsArray, 0, HitsArray.Length);
            }
        }
    }
}
