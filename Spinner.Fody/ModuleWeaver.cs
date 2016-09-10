﻿#define WITH_THREADING

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Mono.Cecil;
using Mono.Cecil.Rocks;
using Spinner.Aspects;
using Spinner.Fody.Analysis;
using Spinner.Fody.Multicasting;
using Spinner.Fody.Utilities;
using Spinner.Fody.Weaving;

namespace Spinner.Fody
{
    public class ModuleWeaver
    {
        private static bool s_test;

        public ModuleDefinition ModuleDefinition { get; set; }

        public Action<string> LogDebug { get; set; }
        
        public Action<string> LogInfo { get; set; }
        
        public Action<string> LogWarning { get; set; }
        
        public Action<string> LogError { get; set; }

        // Definition for IMethodInterceptionAspect
        private ModuleWeavingContext _mwc;
        private MulticastAttributeRegistry _multicastAttributeRegistry;

        public ModuleWeaver()
        {
            LogDebug = s => { };
            LogInfo = s => { };
            LogWarning = s => { };
            LogError = s => { };
        }

        public void Execute()
        {
            LogInfo($"---- Beginning aspect weaving for: {ModuleDefinition.Assembly.FullName} ----");

            AssemblyNameReference spinnerName = ModuleDefinition.AssemblyReferences.FirstOrDefault(a => a.Name == "Spinner");

            if (spinnerName == null)
            {
                LogWarning("No reference to Spinner assembly detected. Doing nothing.");
                return;
            }

            _mwc = new ModuleWeavingContext(this,
                                            ModuleDefinition,
                                            ModuleDefinition.AssemblyResolver.Resolve(spinnerName).MainModule);

            List<TypeDefinition> types = ModuleDefinition.GetAllTypes().ToList();
            var stopwatch = new Stopwatch();

            LogInfo("Beginning attribute multicasting...");

            stopwatch.Start();

            _multicastAttributeRegistry = MulticastAttributeRegistry.Create(_mwc);

            stopwatch.Stop();

            LogInfo($"Finished attribute multicasting in {stopwatch.ElapsedMilliseconds} ms");
            
            // Analyze aspect types in parallel.

            LogInfo("Beginning aspect feature analysis...");

            stopwatch.Restart();

            var analysisLocks = new LockTargetProvider<TypeDefinition>();

            Task[] analysisTasks = types.Select(t => CreateAnalysisAction(t, analysisLocks))
                                        .Where(a => a != null)
                                        .Select(RunTask)
                                        .ToArray();

            if (analysisTasks.Length != 0)
                Task.WhenAll(analysisTasks).Wait();

            stopwatch.Stop();

            LogInfo($"Finished feature analysis for {analysisTasks.Length} types in {stopwatch.ElapsedMilliseconds} ms");

            LogInfo("Beginning aspect weaving...");

            stopwatch.Restart();

            // Execute type weavings in parallel. The ModuleWeavingContext provides thread-safe imports.
            // Weaving does not require any other module-level changes.

            // Tasks are only created when there is actual work to be done for a type.
            Task[] weaveTasks = types.Select(CreateWeaveAction)
                                     .Where(a => a != null)
                                     .Select(RunTask)
                                     .ToArray();

            if (weaveTasks.Length != 0)
                Task.WhenAll(weaveTasks).Wait();

            stopwatch.Stop();

            LogInfo($"Finished aspect weaving for {weaveTasks.Length} types in {stopwatch.ElapsedMilliseconds} ms");
        }

        private static Task RunTask(Action action)
        {
#if WITH_THREADING
            return Task.Run(action);
#else
            action();
            return Task.FromResult(true);
#endif
        }

        private Action CreateWeaveAction(TypeDefinition type)
        {
            // State machine weaving is handled by its owning method. Trying to treat state machines as their own type
            // causes threading issues with the declaring type's weaver.
            if (NameUtility.IsStateMachineName(type.Name))
                return null;

            return () =>
            {
                AspectWeaver[] weavers = AspectWeaverFactory.TryCreate(_mwc, _multicastAttributeRegistry, type);

                if (weavers != null)
                {
                    foreach (AspectWeaver w in weavers)
                        w.Weave();
                }
            };
        }

        private Action CreateAnalysisAction(TypeDefinition type, LockTargetProvider<TypeDefinition> ltp)
        {
            if (!AspectFeatureAnalyzer.IsMaybeAspect(type))
                return null;

            return () =>
            {
                try
                {
                    AspectFeatureAnalyzer.Analyze(_mwc, type, ltp);
                }
                catch (Exception ex)
                {
                    LogError($"Exception while analyzing featores of type {type.Name}: {ex.GetType().Name}: {ex.Message}");
                    LogError(ex.StackTrace);
                    throw;
                }
            };
        }

        private static bool IsAspectAttribute(TypeDefinition attributeType, TypeDefinition aspectType)
        {
            TypeDefinition current = attributeType;
            do
            {
                for (int i = 0; i < current.Interfaces.Count; i++)
                {
                    if (current.Interfaces[i].Resolve() == aspectType)
                        return true;
                }

                current = current.BaseType?.Resolve();
            } while (current != null);

            return false;
        }
    }
}
