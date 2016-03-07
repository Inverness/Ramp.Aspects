using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Mono.Cecil;
using Spinner.Aspects;
using Spinner.Fody.Multicasting;

namespace Spinner.Fody
{
    /// <summary>
    /// Provides global context and functionality for weavers.
    /// </summary>
    internal class ModuleWeavingContext
    {
        /// <summary>
        /// The module currently being weaved.
        /// </summary>
        internal readonly ModuleDefinition Module;

        /// <summary>
        /// Well known Spinner members.
        /// </summary>
        internal readonly WellKnownSpinnerMembers Spinner;

        /// <summary>
        /// Well known .NET members.
        /// </summary>
        internal readonly WellKnownFrameworkMembers Framework;

        private readonly ModuleWeaver _weaver;
        private readonly Dictionary<MethodDefinition, Features> _methodFeatures;
        private readonly Dictionary<MethodDefinition, Features> _typeFeatures;
        private readonly Dictionary<TypeReference, AdviceType> _adviceTypes; 
        private int _aspectIndexCounter;

        internal ModuleWeavingContext(ModuleWeaver weaver, ModuleDefinition module, ModuleDefinition libraryModule)
        {
            _weaver = weaver;
            Module = module;
            Spinner = new WellKnownSpinnerMembers(libraryModule);
            Framework = new WellKnownFrameworkMembers(module);

            _methodFeatures = new Dictionary<MethodDefinition, Features>
            {
                {Spinner.AdviceArgs_Instance.GetMethod, Features.Instance},
                {Spinner.AdviceArgs_Instance.SetMethod, Features.Instance},
                {Spinner.MethodArgs_Arguments.GetMethod, Features.GetArguments},
                {Spinner.LocationInterceptionArgs_Index.GetMethod, Features.GetArguments},
                {Spinner.EventInterceptionArgs_Arguments.GetMethod, Features.GetArguments},
                {Spinner.Arguments_set_Item, Features.SetArguments},
                {Spinner.Arguments_SetValue, Features.SetArguments},
                {Spinner.Arguments_SetValueT, Features.SetArguments},
                {Spinner.MethodExecutionArgs_FlowBehavior.SetMethod, Features.FlowControl},
                {Spinner.MethodExecutionArgs_ReturnValue.GetMethod, Features.ReturnValue},
                {Spinner.MethodExecutionArgs_ReturnValue.SetMethod, Features.ReturnValue},
                {Spinner.EventInterceptionArgs_ReturnValue.GetMethod, Features.ReturnValue},
                {Spinner.EventInterceptionArgs_ReturnValue.SetMethod, Features.ReturnValue},
                {Spinner.MethodExecutionArgs_YieldValue.GetMethod, Features.YieldValue},
                {Spinner.MethodExecutionArgs_YieldValue.SetMethod, Features.YieldValue},
                {Spinner.MethodArgs_Method.GetMethod, Features.MemberInfo},
                {Spinner.LocationInterceptionArgs_Property.GetMethod, Features.MemberInfo},
                {Spinner.EventInterceptionArgs_Event.GetMethod, Features.MemberInfo},
                {Spinner.AdviceArgs_Tag.GetMethod, Features.Tag},
                {Spinner.AdviceArgs_Tag.SetMethod, Features.Tag},
            };

            _typeFeatures = new Dictionary<MethodDefinition, Features>
            {
                {Spinner.IMethodBoundaryAspect_OnEntry, Features.OnEntry},
                {Spinner.IMethodBoundaryAspect_OnExit, Features.OnExit},
                {Spinner.IMethodBoundaryAspect_OnSuccess, Features.OnSuccess},
                {Spinner.IMethodBoundaryAspect_OnException, Features.OnException},
                {Spinner.IMethodBoundaryAspect_OnYield, Features.OnYield},
                {Spinner.IMethodBoundaryAspect_OnResume, Features.OnResume},
                {Spinner.IMethodInterceptionAspect_OnInvoke, Features.None},
                {Spinner.ILocationInterceptionAspect_OnGetValue, Features.None},
                {Spinner.ILocationInterceptionAspect_OnSetValue, Features.None},
                {Spinner.IEventInterceptionAspect_OnAddHandler, Features.None},
                {Spinner.IEventInterceptionAspect_OnRemoveHandler, Features.None},
                {Spinner.IEventInterceptionAspect_OnInvokeHandler, Features.None}
            };

            _adviceTypes = new Dictionary<TypeReference, AdviceType>(new TypeReferenceIsSameComparer())
            {
                {Spinner.MethodEntryAdvice, AdviceType.MethodEntry},
                {Spinner.MethodExitAdvice, AdviceType.MethodExit},
                {Spinner.MethodSuccessAdvice, AdviceType.MethodSuccess},
                {Spinner.MethodExceptionAdvice, AdviceType.MethodException},
                {Spinner.MethodFilterExceptionAdvice, AdviceType.MethodFilterException},
                {Spinner.MethodYieldAdvice, AdviceType.MethodYield},
                {Spinner.MethodResumeAdvice, AdviceType.MethodResume},
                {Spinner.MethodInvokeAdvice, AdviceType.MethodInvoke}
            };

            MulticastEngine = new MulticastEngine(this);
        }

        /// <summary>
        /// Gets a dictionary that maps method definitions to what feature their use in IL indicates.
        /// </summary>
        internal IReadOnlyDictionary<MethodDefinition, Features> MethodFeatures => _methodFeatures;

        /// <summary>
        /// Gets a dictionary that maps aspect interface method definitions to the type feature they indicate support of.
        /// </summary>
        internal IReadOnlyDictionary<MethodDefinition, Features> TypeFeatures => _typeFeatures;

        /// <summary>
        /// Gets a dictionary that maps advice attribute types to the AdviceType enum.
        /// </summary>
        internal IReadOnlyDictionary<TypeReference, AdviceType> AdviceTypes => _adviceTypes;

        internal MulticastEngine MulticastEngine { get; }

        internal TypeReference SafeImport(Type type)
        {
            lock (Module)
                return Module.Import(type);
        }

        internal TypeReference SafeImport(TypeReference type)
        {
            if (type.Module == Module)
                return type;
            lock (Module)
                return Module.Import(type);
        }

        internal MethodReference SafeImport(MethodReference method)
        {
            if (method.Module == Module)
                return method;
            lock (Module)
                return Module.Import(method);
        }

        internal FieldReference SafeImport(FieldReference field)
        {
            if (field.Module == Module)
                return field;
            lock (Module)
                return Module.Import(field);
        }

        internal TypeDefinition SafeGetType(string fullName)
        {
            lock (Module)
                return Module.GetType(fullName);
        }

        [Conditional("DEBUG")]
        internal void LogDebug(string text)
        {
            _weaver.LogDebug(text);
        }

        internal void LogInfo(string text)
        {
            _weaver.LogInfo(text);
        }

        internal void LogWarning(string text)
        {
            _weaver.LogWarning(text);
        }

        internal void LogError(string text)
        {
            _weaver.LogError(text);
        }

        internal int NewAspectIndex()
        {
            return Interlocked.Increment(ref _aspectIndexCounter);
        }

        /// <summary>
        /// Get the features declared for a type. AnalzyedFeaturesAttribute takes precedence over FeaturesAttribute.
        /// </summary>
        internal Features GetFeatures(TypeDefinition aspectType)
        {
            TypeDefinition attrType = Spinner.FeaturesAttribute;
            TypeDefinition analyzedAttrType = Spinner.AnalyzedFeaturesAttribute;

            Features? features = null;

            TypeDefinition current = aspectType;
            while (current != null)
            {
                if (current.HasCustomAttributes)
                {
                    foreach (CustomAttribute a in current.CustomAttributes)
                    {
                        TypeReference atype = a.AttributeType;

                        if (atype.IsSame(analyzedAttrType))
                        {
                            return (Features) (uint) a.ConstructorArguments.First().Value;
                        }

                        if (atype.IsSame(attrType))
                        {
                            features = (Features) (uint) a.ConstructorArguments.First().Value;
                            // Continue in case AnalyzedFeaturesAttribute is found.
                        }
                    }
                }

                // No need to examine base type if found here
                if (features.HasValue)
                    return features.Value;

                current = current.BaseType?.Resolve();
            }

            return Features.None;
        }

        /// <summary>
        /// Get the features declared for an advice. AnalzyedFeaturesAttribute takes precedence over FeaturesAttribute.
        /// </summary>
        internal Features GetFeatures(MethodDefinition advice)
        {
            TypeDefinition attrType = Spinner.FeaturesAttribute;
            TypeDefinition analyzedAttrType = Spinner.AnalyzedFeaturesAttribute;

            Features? features = null;

            MethodDefinition current = advice;
            while (current != null)
            {
                if (current.HasCustomAttributes)
                {
                    foreach (CustomAttribute a in current.CustomAttributes)
                    {
                        TypeReference atype = a.AttributeType;

                        if (atype.IsSame(analyzedAttrType))
                        {
                            return (Features) (uint) a.ConstructorArguments.First().Value;
                        }

                        if (atype.IsSame(attrType))
                        {
                            features = (Features) (uint) a.ConstructorArguments.First().Value;
                            // Continue in case AnalyzedFeaturesAttribute is found on same type.
                        }
                    }
                }

                if (features.HasValue)
                    return features.Value;

                current = current.DeclaringType.BaseType?.Resolve()?.GetMethod(advice, true);
            }

            return Features.None;
        }

        private class TypeReferenceIsSameComparer : IEqualityComparer<TypeReference>
        {
            public bool Equals(TypeReference x, TypeReference y)
            {
                return x.IsSame(y);
            }

            public int GetHashCode(TypeReference obj)
            {
                return obj.Name.GetHashCode();
            }
        }
    }
}