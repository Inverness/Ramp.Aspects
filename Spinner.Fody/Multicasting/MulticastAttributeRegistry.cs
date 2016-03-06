﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Mono.Cecil;
using Spinner.Extensibility;

namespace Spinner.Fody.Multicasting
{
    /// <summary>
    /// Builds and provides a registry that maps custom attribute providers to multicast attribute instances that
    /// apply to them either through direct attribute application, container multicasting, or inheritance.
    /// </summary>
    internal class MulticastAttributeRegistry
    {
        private delegate void ResultHandler(MulticastInstance mi, ICustomAttributeProvider provider);

        private static readonly IList<MulticastInstance> s_noInstances = new MulticastInstance[0];

        private readonly ModuleWeavingContext _mwc;
        private readonly ModuleDefinition _module;
        private readonly TypeDefinition _compilerGeneratedAttributeType;
        private readonly TypeDefinition _multicastAttributeType;

        // The final mapping of attribute providers to the multicast instances that apply to them.
        private readonly Dictionary<ICustomAttributeProvider, List<MulticastInstance>> _targets =
            new Dictionary<ICustomAttributeProvider, List<MulticastInstance>>();

        // Maps a provider to its derived providers. This can include assemblies, types, and methods.
        private Dictionary<ICustomAttributeProvider, List<ICustomAttributeProvider>> _derived =
            new Dictionary<ICustomAttributeProvider, List<ICustomAttributeProvider>>();

        // Maps providers that define actual multicast attributes to their instances. An ordering integer is used to
        // ensure that multicasts are processed in order from base to derived.
        private Dictionary<ICustomAttributeProvider, Tuple<int, List<MulticastInstance>>> _instances =
            new Dictionary<ICustomAttributeProvider, Tuple<int, List<MulticastInstance>>>();

        private int _inheritOrderCounter = int.MinValue;
        private int _directOrderCounter;

        private MulticastAttributeRegistry(ModuleWeavingContext mwc)
        {
            _mwc = mwc;
            _module = mwc.Module;
            _compilerGeneratedAttributeType = mwc.Framework.CompilerGeneratedAttribute;
            _multicastAttributeType = mwc.Spinner.MulticastAttribute;
        }

        internal IList<MulticastInstance> GetMulticasts(ICustomAttributeProvider provider)
        {
            List<MulticastInstance> multicasts;
            // ReSharper disable once InconsistentlySynchronizedField
            return _targets.TryGetValue(provider, out multicasts) ? multicasts : s_noInstances;
        }

        internal static MulticastAttributeRegistry Create(ModuleWeavingContext mwc)
        {
            var inst = new MulticastAttributeRegistry(mwc);
            inst.Initialize();
            return inst;
        }

        private void Initialize()
        {
            //
            // Creates multicast attribute instances for all types in assembly and referenced assemblies. Also for
            // types that have multicast 
            //

            var filter = new HashSet<ICustomAttributeProvider>();
            InstantiateMulticasts(_module.Assembly, null, filter);

            //
            // Identify all types and assemblies that provide multicast attributes
            //

            filter.Clear();
            AddDerivedProviders(_module.Assembly, null, filter);

            //
            // Create new instances where inheritance is allowed
            //

            foreach (KeyValuePair<ICustomAttributeProvider, Tuple<int, List<MulticastInstance>>> item in _instances.ToList())
            {
                List<ICustomAttributeProvider> derivedList;
                if (!_derived.TryGetValue(item.Key, out derivedList) || derivedList == null)
                    continue;

                foreach (MulticastInstance mi in item.Value.Item2)
                {
                    if (mi.Inheritance == MulticastInheritance.None)
                        continue;

                    foreach (ICustomAttributeProvider d in derivedList)
                    {
                        MulticastInstance nmi = mi.WithTarget(d);

                        Tuple<int, List<MulticastInstance>> mis;
                        if (!_instances.TryGetValue(d, out mis))
                        {
                            // Inherit order counter ensures that inherited instances are applied first
                            int initOrder = _inheritOrderCounter++;
                            mis = Tuple.Create(initOrder, new List<MulticastInstance>());
                            _instances.Add(d, mis);
                        }

                        mis.Item2.Add(nmi);

                        _mwc.LogDebug($"Multicast Inheritance: AttributeType: {mi.AttributeType}, Origin: {mi.Origin}, Inheritor: {d}");
                    }
                }
            }

            //
            // Apply multicasts in the order the instances were created
            //

            var initLists = _instances.Values.ToList();
            initLists.Sort((a, b) => a.Item1.CompareTo(b.Item1));

            foreach (Tuple<int, List<MulticastInstance>> group in initLists)
                UpdateMulticastTargets(group.Item2);

            //
            // Apply ordering and exclusions
            //

            foreach (KeyValuePair<ICustomAttributeProvider, List<MulticastInstance>> item in _targets)
            {
                List<MulticastInstance> instances = item.Value;

                if (instances.Count > 1)
                {
                    // Remove duplicates inherited from same origin and order by priority
                    // LINQ ensures that things remain in the original order where possible
                    List<MulticastInstance> newInstances = instances.Distinct().OrderBy(i => i.Priority).ToList();
                    instances.Clear();
                    instances.AddRange(newInstances);
                }
                
                // Apply exclusions
                for (int i = 0; i < instances.Count; i++)
                {
                    MulticastInstance a = instances[i];

                    if (a.Exclude)
                    {
                        for (int r = instances.Count - 1; r > -1; r--)
                        {
                            if (instances[r].AttributeType == a.AttributeType)
                                instances.RemoveAt(r);
                        }

                        i = -1;
                    }
                }
            }

            // No longer need this data
            _derived = null;
            _instances = null;
        }

        private void AddDerivedProviders(
            AssemblyDefinition assembly,
            AssemblyDefinition referencer,
            HashSet<ICustomAttributeProvider> filter)
        {
            if (filter.Contains(assembly))
                return;
            filter.Add(assembly);

            if (!IsSpinnerOrReferencesSpinner(assembly))
                return;

            if (referencer != null)
                TryAddDerivedProvider(assembly, referencer);

            foreach (AssemblyNameReference ar in assembly.MainModule.AssemblyReferences)
            {
                if (!IsFrameworkAssemblyReference(ar))
                    AddDerivedProviders(assembly.MainModule.AssemblyResolver.Resolve(ar), assembly, filter);
            }

            foreach (TypeDefinition t in assembly.MainModule.Types)
            {
                AddDerivedProviders(t, filter);
            }
        }

        private void AddDerivedProviders(TypeDefinition type, HashSet<ICustomAttributeProvider> filter)
        {
            if (!filter.Contains(type))
                AddDerivedProvidersSlow(type, filter);
        }

        private void AddDerivedProvidersSlow(TypeDefinition type, HashSet<ICustomAttributeProvider> filter)
        {
            filter.Add(type);

            if (type.BaseType != null && type.BaseType != type.Module.TypeSystem.Object)
            {
                TypeDefinition baseType = type.BaseType.Resolve();

                if (_derived.ContainsKey(baseType) && !_derived.ContainsKey(type))
                    _derived.Add(type, null);

                AddDerivedProviders(baseType, filter);

                TryAddDerivedProvider(baseType, type);
            }

            if (type.HasInterfaces)
            {
                foreach (TypeReference itr in type.Interfaces)
                {
                    TypeDefinition it = itr.Resolve();

                    if (_derived.ContainsKey(it) && !_derived.ContainsKey(type))
                        _derived.Add(type, null);

                    AddDerivedProviders(it, filter);

                    TryAddDerivedProvider(it, type);
                }
            }

            if (type.HasMethods)
            {
                var overrides = new List<MethodDefinition>();

                foreach (MethodDefinition m in type.Methods)
                {
                    GetOverrides(m, overrides);

                    if (overrides.Count != 0)
                    {
                        foreach (MethodDefinition ov in overrides)
                        {
                            if (_derived.ContainsKey(ov) && !_derived.ContainsKey(m))
                                _derived.Add(m, null);

                            TryAddDerivedProvider(ov, m);
                            TryAddDerivedProvider(ov.MethodReturnType, m.MethodReturnType);

                            if (m.HasParameters)
                            {
                                for (int i = 0; i < m.Parameters.Count; i++)
                                    TryAddDerivedProvider(ov.Parameters[i], m.Parameters[i]);
                            }
                        }

                        overrides.Clear();
                    }
                }
            }

            if (type.HasNestedTypes)
            {
                foreach (TypeDefinition nt in type.NestedTypes)
                    AddDerivedProviders(nt, filter);
            }
        }

        private void GetOverrides(MethodDefinition m, List<MethodDefinition> results)
        {
            TypeDefinition type = m.DeclaringType;

            TypeDefinition baseType = type.BaseType?.Resolve();
            if (baseType != null && baseType != type.Module.TypeSystem.Object)
            {
                MethodDefinition ovr = baseType.GetMethod(m, false);
                if (ovr != null)
                {
                    results.Add(ovr);
                }
            }

            if (type.HasInterfaces)
            {
                foreach (TypeReference ir in type.Interfaces)
                {
                    TypeDefinition id = ir.Resolve();

                    MethodDefinition ovr = id.GetMethod(m, false);

                    if (ovr != null)
                        results.Add(ovr);
                }
            }
        } 

        private void InstantiateMulticasts(
            AssemblyDefinition assembly,
            AssemblyDefinition referencer,
            HashSet<ICustomAttributeProvider> filter)
        {
            if (filter.Contains(assembly))
                return;
            filter.Add(assembly);

            if (!IsSpinnerOrReferencesSpinner(assembly))
                return;

            foreach (AssemblyNameReference ar in assembly.MainModule.AssemblyReferences)
            {
                // Skip some of the big ones
                if (IsFrameworkAssemblyReference(ar))
                    continue;

                AssemblyDefinition referencedAssembly = assembly.MainModule.AssemblyResolver.Resolve(ar);

                InstantiateMulticasts(referencedAssembly, assembly, filter);
            }

            if (assembly.HasCustomAttributes)
            {
                var attrs = new List<MulticastInstance>();
                InstantiateMulticasts(assembly, ProviderType.Assembly);
                if (referencer != null)
                    TryAddDerivedProvider(assembly, referencer);
                UpdateMulticastTargets(attrs);
            }

            foreach (TypeDefinition t in assembly.MainModule.Types)
            {
                if (!HasGeneratedName(t) && !HasGeneratedAttribute(t))
                    InstantiateMulticasts(t, filter);
            }
        }

        private void InstantiateMulticasts(TypeDefinition type, HashSet<ICustomAttributeProvider> filter)
        {
            if (!filter.Contains(type))
                InstantiateMulticastsSlow(type, filter);
        }

        private void InstantiateMulticastsSlow(TypeDefinition type, HashSet<ICustomAttributeProvider> filter)
        {
            filter.Add(type);

            if (type.BaseType != null)
                InstantiateMulticasts(type.BaseType.Resolve(), filter);

            if (type.HasInterfaces)
                foreach (TypeReference itr in type.Interfaces)
                    InstantiateMulticasts(itr.Resolve(), filter);

            if (type.HasCustomAttributes)
                InstantiateMulticasts(type, ProviderType.Type);

            if (type.HasMethods)
            {
                foreach (MethodDefinition m in type.Methods)
                {
                    if (HasGeneratedName(m))
                        continue;

                    // getters, setters, and event adders and removers are handled by their owning property/event
                    if (m.SemanticsAttributes != MethodSemanticsAttributes.None)
                        continue;

                    if (m.HasCustomAttributes)
                        InstantiateMulticasts(m, ProviderType.Method);

                    if (m.HasParameters)
                    {
                        foreach (ParameterDefinition p in m.Parameters)
                        {
                            if (p.HasCustomAttributes)
                                InstantiateMulticasts(p, ProviderType.Parameter);
                        }
                    }

                    if (m.MethodReturnType.HasCustomAttributes && !m.ReturnType.IsSame(m.Module.TypeSystem.Void))
                        InstantiateMulticasts(m.MethodReturnType, ProviderType.MethodReturn);
                }
            }

            if (type.HasProperties)
            {
                foreach (PropertyDefinition p in type.Properties)
                {
                    if (HasGeneratedName(p))
                        continue;

                    if (p.HasCustomAttributes)
                        InstantiateMulticasts(p, ProviderType.Property);

                    if (p.GetMethod != null)
                        InstantiateMulticasts(p.GetMethod, ProviderType.Method);

                    if (p.SetMethod != null)
                        InstantiateMulticasts(p.SetMethod, ProviderType.Method);
                }
            }

            if (type.HasEvents)
            {
                foreach (EventDefinition e in type.Events)
                {
                    if (HasGeneratedName(e))
                        continue;

                    if (e.HasCustomAttributes)
                        InstantiateMulticasts(e, ProviderType.Event);

                    if (e.AddMethod != null)
                        InstantiateMulticasts(e.AddMethod, ProviderType.Method);

                    if (e.RemoveMethod != null)
                        InstantiateMulticasts(e.RemoveMethod, ProviderType.Method);
                }
            }

            if (type.HasFields)
            {
                foreach (FieldDefinition f in type.Fields)
                {
                    if (!HasGeneratedName(f) && f.HasCustomAttributes)
                        InstantiateMulticasts(f, ProviderType.Field);
                }
            }

            if (type.HasNestedTypes)
            {
                foreach (TypeDefinition nt in type.NestedTypes)
                {
                    if (!HasGeneratedName(nt) && !HasGeneratedAttribute(nt))
                        InstantiateMulticasts(nt, filter);
                }
            }
        }
        
        private void TryAddDerivedProvider(ICustomAttributeProvider baseType, ICustomAttributeProvider derivedType)
        {
            List<ICustomAttributeProvider> derivedTypes;
            if (!_derived.TryGetValue(baseType, out derivedTypes))
                return;

            if (derivedTypes == null)
            {
                derivedTypes = new List<ICustomAttributeProvider>();
                _derived[baseType] = derivedTypes;
            }

            derivedTypes.Add(derivedType);
        }

        /// <summary>
        /// Process a multicast instance list by ordering them, applying exclusion rules, and removing instances
        /// inherited from the same origin.
        /// </summary>
        /// <param name="instances"></param>
        private void UpdateMulticastTargets(List<MulticastInstance> instances)
        {
            if (instances != null && instances.Count != 0)
            {
                foreach (MulticastInstance mi in instances)
                    UpdateMulticastTargets(mi);
            }
        }

        private void InstantiateMulticasts(ICustomAttributeProvider origin, ProviderType originType)
        {
            List<MulticastInstance> results = null;

            foreach (CustomAttribute a in origin.CustomAttributes)
            {
                TypeDefinition atype = a.AttributeType.Resolve();

                if (atype.IsSame(_compilerGeneratedAttributeType))
                    break;

                if (!IsMulticastAttribute(atype))
                    continue;

                var mai = new MulticastInstance(origin, originType, a, atype);

                if (results == null)
                    results = new List<MulticastInstance>();
                results.Add(mai);
            }

            if (results == null)
                return;

            int initOrder = _directOrderCounter++;

            _derived.Add(origin, null);
            _instances.Add(origin, Tuple.Create(initOrder, results));
        }

        private void UpdateMulticastTargets(MulticastInstance mi)
        {
            List<Tuple<MulticastInstance, ICustomAttributeProvider>> indirect = null;

            // Find indirect multicasts up the tree. Indirect meaning targets where a multicast attribute was not
            // directly specified in the source code.
            if (mi.Origin == mi.Target || mi.Inheritance == MulticastInheritance.Multicast)
            {
                indirect = new List<Tuple<MulticastInstance, ICustomAttributeProvider>>();

                ResultHandler resultHandler = (i, p) => indirect.Add(Tuple.Create(i, p));

                MulticastAttributes memberCompareAttrs = mi.Attribute.Constructor.Module != _module
                                                             ? mi.TargetExternalMemberAttributes
                                                             : mi.TargetMemberAttributes;

                switch (mi.TargetType)
                {
                    case ProviderType.Assembly:
                        GetIndirectMulticastTargets(mi, (AssemblyDefinition) mi.Target, resultHandler);
                        break;
                    case ProviderType.Type:
                        GetIndirectMulticastTargets(mi, (TypeDefinition) mi.Target, resultHandler);
                        break;
                    case ProviderType.Method:
                        if (memberCompareAttrs != 0)
                        {
                            // Methods with semantic attributes are excluded because these are handled by their parent
                            // property or event.
                            var method = (MethodDefinition) mi.Target;
                            if (method.SemanticsAttributes == MethodSemanticsAttributes.None)
                                GetIndirectMulticastTargets(mi, method, resultHandler);
                        }
                        break;
                    case ProviderType.Property:
                        if (memberCompareAttrs != 0)
                            GetIndirectMulticastTargets(mi, (PropertyDefinition) mi.Target, resultHandler);
                        break;
                    case ProviderType.Event:
                        if (memberCompareAttrs != 0)
                            GetIndirectMulticastTargets(mi, (EventDefinition) mi.Target, resultHandler);
                        break;
                    case ProviderType.Field:
                        if (memberCompareAttrs != 0)
                            GetIndirectMulticastTargets(mi, (FieldDefinition) mi.Target, resultHandler);
                        break;
                    case ProviderType.Parameter:
                    case ProviderType.MethodReturn:
                        // these are handled by their parent method
                        break;
                }
            }

            if (indirect != null)
            {
                foreach (Tuple<MulticastInstance, ICustomAttributeProvider> item in indirect)
                    AddMulticastTarget(item.Item1, item.Item2);
            }

            if ((mi.TargetElements & mi.Target.GetMulticastTargetType()) != 0)
                AddMulticastTarget(mi, mi.Target);
        }

        private void AddMulticastTarget(MulticastInstance mi, ICustomAttributeProvider target)
        {
            List<MulticastInstance> current;
            if (!_targets.TryGetValue(target, out current))
            {
                current = new List<MulticastInstance>();
                _targets.Add(target, current);
            }
            current.Add(mi);
        }

        /// <summary>
        /// Gets indirect multicasts for an assembly and its types.
        /// </summary>
        private void GetIndirectMulticastTargets(MulticastInstance mi, AssemblyDefinition assembly, ResultHandler handler)
        {
            if ((mi.TargetElements & MulticastTargets.Assembly) != 0 && mi.TargetAssemblies.IsMatch(assembly.FullName))
                handler(mi, assembly);

            foreach (TypeDefinition type in assembly.MainModule.Types)
                GetIndirectMulticastTargets(mi, type, handler);
        }

        /// <summary>
        /// Gets indirect multicasts for a type and its members.
        /// </summary>
        private void GetIndirectMulticastTargets(MulticastInstance mi, TypeDefinition type, ResultHandler handler)
        {
            const MulticastTargets typeChildTargets =
                MulticastTargets.AnyMember | MulticastTargets.Parameter | MulticastTargets.ReturnValue;

            const MulticastTargets methodAndChildTargets =
                MulticastTargets.Method | MulticastTargets.Parameter | MulticastTargets.ReturnValue;

            const MulticastTargets propertyAndChildTargets =
                MulticastTargets.Property | methodAndChildTargets;

            const MulticastTargets eventAndChildTargets =
                MulticastTargets.Event | methodAndChildTargets;

            MulticastTargets typeTargetType = type.GetMulticastTargetType();

            if ((mi.TargetElements & (typeTargetType | typeChildTargets)) == 0)
                return;

            MulticastAttributes attrs = ComputeMulticastAttributes(type);

            bool external = mi.Attribute.Constructor.Module != _module;

            MulticastAttributes compareAttrs = external
                ? mi.TargetExternalTypeAttributes
                : mi.TargetTypeAttributes;

            if ((compareAttrs & attrs) == 0)
                return;

            if (!mi.TargetTypes.IsMatch(type.FullName))
                return;

            if ((mi.TargetElements & typeTargetType) != 0)
                handler(mi, type);

            MulticastAttributes memberCompareAttrs = external
                ? mi.TargetExternalMemberAttributes
                : mi.TargetMemberAttributes;

            // If no members then don't continue.
            if (memberCompareAttrs == 0)
                return;

            if (type.HasMethods && (mi.TargetElements & methodAndChildTargets) != 0)
            {
                foreach (MethodDefinition method in type.Methods)
                    GetIndirectMulticastTargets(mi, method, handler);
            }

            if (type.HasProperties && (mi.TargetElements & propertyAndChildTargets) != 0)
            {
                foreach (PropertyDefinition property in type.Properties)
                    GetIndirectMulticastTargets(mi, property, handler);
            }

            if (type.HasEvents && (mi.TargetElements & eventAndChildTargets) != 0)
            {
                foreach (EventDefinition evt in type.Events)
                    GetIndirectMulticastTargets(mi, evt, handler);
            }

            if (type.HasFields && (mi.TargetElements & MulticastTargets.Field) != 0)
            {
                foreach (FieldDefinition field in type.Fields)
                    GetIndirectMulticastTargets(mi, field, handler);
            }

            if (type.HasNestedTypes)
            {
                foreach (TypeDefinition nestedType in type.NestedTypes)
                    GetIndirectMulticastTargets(mi, nestedType, handler);
            }
        }

        private void GetIndirectMulticastTargets(MulticastInstance mi, MethodDefinition method, ResultHandler handler)
        {
            const MulticastTargets childTargets = MulticastTargets.ReturnValue | MulticastTargets.Parameter;

            MulticastTargets methodTargetType = method.GetMulticastTargetType();

            // Stop if the attribute does not apply to this method, the return value, or parameters.
            if ((mi.TargetElements & (methodTargetType | childTargets)) == 0)
                return;

            // If member name and attributes check fails, then it cannot apply to return value or parameters
            if (!IsValidMemberAttributes(mi, method))
                return;

            // If this is not the child of a property or event, compare the name.
            bool hasParent = method.SemanticsAttributes != MethodSemanticsAttributes.None;
            if (!hasParent && !mi.TargetMembers.IsMatch(method.Name))
                return;

            if ((mi.TargetElements & methodTargetType) != 0)
                handler(mi, method);

            if ((mi.TargetElements & MulticastTargets.ReturnValue) != 0)
                handler(mi, method.MethodReturnType);

            if (method.HasParameters && (mi.TargetElements & MulticastTargets.Parameter) != 0)
            {
                foreach (ParameterDefinition parameter in method.Parameters)
                {
                    MulticastAttributes pattrs = ComputeMulticastAttributes(parameter);

                    if ((mi.TargetParameterAttributes & pattrs) != 0 && mi.TargetParameters.IsMatch(parameter.Name))
                    {
                        handler(mi, parameter);
                    }
                }
            }
        }

        private void GetIndirectMulticastTargets(MulticastInstance mi, PropertyDefinition property, ResultHandler handler)
        {
            if ((mi.TargetElements & (MulticastTargets.Property | MulticastTargets.Method)) == 0)
                return;

            if (!IsValidMemberAttributes(mi, property) || !mi.TargetMembers.IsMatch(property.Name))
                return;

            if ((mi.TargetElements & MulticastTargets.Property) != 0)
                handler(mi, property);

            if ((mi.TargetElements & MulticastTargets.Method) != 0)
            {
                if (property.GetMethod != null)
                    GetIndirectMulticastTargets(mi, property.GetMethod, handler);

                if (property.SetMethod != null)
                    GetIndirectMulticastTargets(mi, property.SetMethod, handler);
            }
        }

        private void GetIndirectMulticastTargets(MulticastInstance mi, EventDefinition evt, ResultHandler handler)
        {
            if ((mi.TargetElements & (MulticastTargets.Event | MulticastTargets.Method)) == 0)
                return;

            if (!IsValidMemberAttributes(mi, evt) || !mi.TargetMembers.IsMatch(evt.Name))
                return;

            if ((mi.TargetElements & MulticastTargets.Event) != 0)
                handler(mi, evt);

            if ((mi.TargetElements & MulticastTargets.Method) != 0)
            {
                if (evt.AddMethod != null)
                    GetIndirectMulticastTargets(mi, evt.AddMethod, handler);

                if (evt.RemoveMethod != null)
                    GetIndirectMulticastTargets(mi, evt.RemoveMethod, handler);
            }
        }

        private void GetIndirectMulticastTargets(MulticastInstance mi, FieldDefinition field, ResultHandler handler)
        {
            if ((mi.TargetElements & MulticastTargets.Field) == 0)
                return;

            if (!IsValidMemberAttributes(mi, field) || !mi.TargetMembers.IsMatch(field.Name))
                return;

            handler(mi, field);
        }

        private bool IsValidMemberAttributes(MulticastInstance mi, IMemberDefinition member)
        {
            MulticastAttributes attrs = ComputeMulticastAttributes(member);

            bool external = mi.Attribute.Constructor.Module != _module;

            MulticastAttributes compareAttrs = external
                ? mi.TargetExternalMemberAttributes
                : mi.TargetMemberAttributes;

            return (compareAttrs & attrs) != 0;
        }

        private MulticastAttributes ComputeMulticastAttributes(TypeDefinition type)
        {
            MulticastAttributes a = 0;

            a |= type.IsAbstract ? MulticastAttributes.Abstract : MulticastAttributes.NonAbstract;

            a |= type.IsAbstract && type.IsSealed ? MulticastAttributes.Static : MulticastAttributes.Instance;

            if (type.IsPublic || type.IsNestedPublic)
            {
                a |= MulticastAttributes.Public;
            }
            else if (type.IsNestedAssembly || type.IsNotPublic)
            {
                a |= MulticastAttributes.Internal;
            }
            else if (type.IsNestedFamily)
            {
                a |= MulticastAttributes.Protected;
            }
            else if (type.IsNestedFamilyAndAssembly)
            {
                a |= MulticastAttributes.InternalAndProtected;
            }
            else if (type.IsNestedFamilyOrAssembly)
            {
                a |= MulticastAttributes.InternalOrProtected;
            }

            a |= HasGeneratedName(type) || HasGeneratedAttribute(type) ? MulticastAttributes.CompilerGenerated : MulticastAttributes.UserGenerated;

            return a;
        }

        private MulticastAttributes ComputeMulticastAttributes(IMemberDefinition member)
        {
            switch (member.GetProviderType())
            {
                case ProviderType.Method:
                    return ComputeMulticastAttributes((MethodDefinition) member);
                case ProviderType.Property:
                    return ComputeMulticastAttributes((PropertyDefinition) member);
                case ProviderType.Event:
                    return ComputeMulticastAttributes((EventDefinition) member);
                case ProviderType.Field:
                    return ComputeMulticastAttributes((FieldDefinition) member);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        private MulticastAttributes ComputeMulticastAttributes(MethodDefinition method)
        {
            MulticastAttributes a = 0;

            if (method.IsPublic)
                a |= MulticastAttributes.Public;
            else if (method.IsFamilyAndAssembly)
                a |= MulticastAttributes.InternalAndProtected;
            else if (method.IsFamilyOrAssembly)
                a |= MulticastAttributes.InternalOrProtected;
            else if (method.IsAssembly)
                a |= MulticastAttributes.Internal;
            else if (method.IsFamily)
                a |= MulticastAttributes.Protected;
            else if (method.IsPrivate)
                a |= MulticastAttributes.Private;

            a |= method.IsStatic ? MulticastAttributes.Static : MulticastAttributes.Instance;

            a |= method.IsAbstract ? MulticastAttributes.Abstract : MulticastAttributes.NonAbstract;

            a |= method.IsVirtual ? MulticastAttributes.Virtual : MulticastAttributes.NonVirtual;

            a |= method.IsManaged ? MulticastAttributes.Managed : MulticastAttributes.NonManaged;

            a |= HasGeneratedName(method) || HasGeneratedAttribute(method) ? MulticastAttributes.CompilerGenerated : MulticastAttributes.UserGenerated;

            return a;
        }

        private MulticastAttributes ComputeMulticastAttributes(PropertyDefinition property)
        {
            MulticastAttributes ga = property.GetMethod != null ? ComputeMulticastAttributes(property.GetMethod) : 0;
            MulticastAttributes sa = property.SetMethod != null ? ComputeMulticastAttributes(property.SetMethod) : 0;

            MulticastAttributes a = 0;

            if ((ga & MulticastAttributes.Public) != 0 || (sa & MulticastAttributes.Public) != 0)
                a |= MulticastAttributes.Public;
            else if ((ga & MulticastAttributes.InternalOrProtected) != 0 || (sa & MulticastAttributes.InternalOrProtected) != 0)
                a |= MulticastAttributes.InternalOrProtected;
            else if ((ga & MulticastAttributes.InternalAndProtected) != 0 || (sa & MulticastAttributes.InternalAndProtected) != 0)
                a |= MulticastAttributes.InternalAndProtected;
            else if ((ga & MulticastAttributes.Internal) != 0 || (sa & MulticastAttributes.Internal) != 0)
                a |= MulticastAttributes.Internal;
            else if ((ga & MulticastAttributes.Protected) != 0 || (sa & MulticastAttributes.Protected) != 0)
                a |= MulticastAttributes.Protected;
            else if ((ga & MulticastAttributes.Private) != 0 || (sa & MulticastAttributes.Private) != 0)
                a |= MulticastAttributes.Private;

            a |= ((ga | sa) & MulticastAttributes.Static) != 0 ? MulticastAttributes.Static : MulticastAttributes.Instance;

            a |= ((ga | sa) & MulticastAttributes.Abstract) != 0 ? MulticastAttributes.Abstract : MulticastAttributes.NonAbstract;

            a |= ((ga | sa) & MulticastAttributes.Virtual) != 0 ? MulticastAttributes.Virtual : MulticastAttributes.NonVirtual;

            a |= ((ga | sa) & MulticastAttributes.Managed) != 0 ? MulticastAttributes.Managed : MulticastAttributes.NonManaged;

            a |= HasGeneratedName(property) || ((ga | sa) & MulticastAttributes.CompilerGenerated) != 0 ? MulticastAttributes.CompilerGenerated : MulticastAttributes.UserGenerated;

            return a;
        }

        private MulticastAttributes ComputeMulticastAttributes(EventDefinition evt)
        {
            Debug.Assert(evt.AddMethod != null);

            return ComputeMulticastAttributes(evt.AddMethod);
        }

        private MulticastAttributes ComputeMulticastAttributes(FieldDefinition field)
        {
            MulticastAttributes a = 0;

            if (field.IsPublic)
                a |= MulticastAttributes.Public;
            else if (field.IsFamilyAndAssembly)
                a |= MulticastAttributes.InternalAndProtected;
            else if (field.IsFamilyOrAssembly)
                a |= MulticastAttributes.InternalOrProtected;
            else if (field.IsAssembly)
                a |= MulticastAttributes.Internal;
            else if (field.IsFamily)
                a |= MulticastAttributes.Protected;
            else if (field.IsPrivate)
                a |= MulticastAttributes.Private;

            a |= field.IsStatic ? MulticastAttributes.Static : MulticastAttributes.Instance;

            a |= field.IsLiteral ? MulticastAttributes.Literal : MulticastAttributes.NonLiteral;

            a |= HasGeneratedName(field) || HasGeneratedAttribute(field) ? MulticastAttributes.CompilerGenerated : MulticastAttributes.UserGenerated;

            return a;
        }

        private MulticastAttributes ComputeMulticastAttributes(ParameterDefinition parameter)
        {
            MulticastAttributes a = 0;

            if (parameter.IsOut)
                a |= MulticastAttributes.OutParameter;
            else if (parameter.ParameterType.IsByReference)
                a |= MulticastAttributes.RefParameter;
            else
                a |= MulticastAttributes.InParameter;

            return a;
        }

        private bool IsMulticastAttribute(TypeDefinition type)
        {
            TypeReference current = type.BaseType;
            while (current != null)
            {
                if (current.IsSame(_multicastAttributeType))
                    return true;

                current = current.Resolve().BaseType;
            }

            return false;
        }

        private static bool HasGeneratedName(IMemberDefinition def)
        {
            return def.Name.StartsWith("<") || def.Name.StartsWith("CS$");
        }

        private bool HasGeneratedAttribute(ICustomAttributeProvider target)
        {
            if (target.HasCustomAttributes)
            {
                foreach (CustomAttribute a in target.CustomAttributes)
                {
                    if (a.AttributeType.IsSame(_compilerGeneratedAttributeType))
                        return true;
                }
            }

            return false;
        }

        private static bool IsFrameworkAssemblyReference(AssemblyNameReference ar)
        {
            return ar.Name.StartsWith("System.") || ar.Name == "System" || ar.Name == "mscorlib";
        }

        private static bool IsSpinnerOrReferencesSpinner(AssemblyDefinition assembly)
        {
            return assembly.Name.Name == "Spinner" || assembly.MainModule.AssemblyReferences.Any(ar => ar.Name == "Spinner");
        }
    }
}
