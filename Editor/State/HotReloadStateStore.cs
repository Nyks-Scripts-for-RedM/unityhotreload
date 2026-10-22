// Copyright 2026 Togan (nyks000)
// Licensed under the Apache License, Version 2.0. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;

namespace Nyks.HotReload.Editor.State
{
    /// <summary>
    /// Metadata for a field dynamically added to a class during a Hot Reload session.
    /// Used by the Inspector extension to render live editable controls in the Unity Inspector.
    /// </summary>
    public sealed class DynamicFieldDescriptor
    {
        public string OwnerTypeFullName { get; set; }
        public string FieldName { get; set; }
        public string DeclaredTypeName { get; set; }
        public string FieldKey { get; set; }
        public Type FieldType { get; set; }
        public object DefaultValue { get; set; }
        public bool IsStatic { get; set; }
        public bool IsPublicOrSerialized { get; set; }
        public bool RequiresUnityObjectSerialization { get; set; }
    }

    /// <summary>
    /// Zero-leak virtual field backing store using <see cref="ConditionalWeakTable{TKey, TValue}"/>.
    /// Preserves the exact memory layout of live MonoBehaviour instances when new fields are added during Play Mode.
    /// </summary>
    public static class HotReloadStateStore
    {
        private static readonly ConditionalWeakTable<object, Dictionary<string, object>> _instanceStore =
            new ConditionalWeakTable<object, Dictionary<string, object>>();

        private static readonly Dictionary<string, object> _staticStore =
            new Dictionary<string, object>(StringComparer.Ordinal);

        private static readonly Dictionary<string, List<DynamicFieldDescriptor>> _typeDescriptors =
            new Dictionary<string, List<DynamicFieldDescriptor>>(StringComparer.Ordinal);

        private static readonly object _syncRoot = new object();

        /// <summary>
        /// Gets the value of a dynamically added instance field, initializing it with <paramref name="defaultValue"/> on first access.
        /// </summary>
        public static T Get<T>(object instance, string fieldKey, T defaultValue = default)
        {
            if (instance == null)
            {
                return defaultValue;
            }

            lock (_syncRoot)
            {
                if (!_instanceStore.TryGetValue(instance, out Dictionary<string, object> fieldMap))
                {
                    fieldMap = new Dictionary<string, object>(StringComparer.Ordinal);
                    _instanceStore.Add(instance, fieldMap);
                }

                if (fieldMap.TryGetValue(fieldKey, out object rawValue))
                {
                    if (rawValue is T typedValue)
                    {
                        return typedValue;
                    }

                    try
                    {
                        return (T)Convert.ChangeType(rawValue, typeof(T));
                    }
                    catch
                    {
                        return defaultValue;
                    }
                }

                fieldMap[fieldKey] = defaultValue;
                return defaultValue;
            }
        }

        /// <summary>
        /// Sets the value of a dynamically added instance field and returns the assigned value.
        /// </summary>
        public static T Set<T>(object instance, string fieldKey, T value)
        {
            if (instance == null)
            {
                return value;
            }

            lock (_syncRoot)
            {
                if (!_instanceStore.TryGetValue(instance, out Dictionary<string, object> fieldMap))
                {
                    fieldMap = new Dictionary<string, object>(StringComparer.Ordinal);
                    _instanceStore.Add(instance, fieldMap);
                }

                fieldMap[fieldKey] = value;
                return value;
            }
        }

        /// <summary>
        /// Untyped getter used by the Unity Inspector drawer for dynamically added fields.
        /// </summary>
        public static object GetBoxed(object instance, DynamicFieldDescriptor descriptor)
        {
            if (descriptor == null)
            {
                return null;
            }

            if (descriptor.IsStatic)
            {
                lock (_syncRoot)
                {
                    if (_staticStore.TryGetValue(descriptor.FieldKey, out object val))
                    {
                        return val;
                    }
                    _staticStore[descriptor.FieldKey] = descriptor.DefaultValue;
                    return descriptor.DefaultValue;
                }
            }

            if (instance == null)
            {
                return descriptor.DefaultValue;
            }

            lock (_syncRoot)
            {
                if (!_instanceStore.TryGetValue(instance, out Dictionary<string, object> fieldMap))
                {
                    fieldMap = new Dictionary<string, object>(StringComparer.Ordinal);
                    _instanceStore.Add(instance, fieldMap);
                }

                if (fieldMap.TryGetValue(descriptor.FieldKey, out object val))
                {
                    return val;
                }

                fieldMap[descriptor.FieldKey] = descriptor.DefaultValue;
                return descriptor.DefaultValue;
            }
        }

        /// <summary>
        /// Untyped setter used by the Unity Inspector drawer for dynamically added fields.
        /// </summary>
        public static void SetBoxed(object instance, DynamicFieldDescriptor descriptor, object value)
        {
            if (descriptor == null)
            {
                return;
            }

            if (descriptor.IsStatic)
            {
                lock (_syncRoot)
                {
                    _staticStore[descriptor.FieldKey] = value;
                }
                return;
            }

            if (instance == null)
            {
                return;
            }

            lock (_syncRoot)
            {
                if (!_instanceStore.TryGetValue(instance, out Dictionary<string, object> fieldMap))
                {
                    fieldMap = new Dictionary<string, object>(StringComparer.Ordinal);
                    _instanceStore.Add(instance, fieldMap);
                }

                fieldMap[descriptor.FieldKey] = value;
            }
        }

        /// <summary>
        /// Gets the value of a dynamically added static field.
        /// </summary>
        public static T GetStatic<T>(string fieldKey, T defaultValue = default)
        {
            lock (_syncRoot)
            {
                if (_staticStore.TryGetValue(fieldKey, out object rawValue) && rawValue is T typedValue)
                {
                    return typedValue;
                }

                _staticStore[fieldKey] = defaultValue;
                return defaultValue;
            }
        }

        /// <summary>
        /// Sets the value of a dynamically added static field.
        /// </summary>
        public static T SetStatic<T>(string fieldKey, T value)
        {
            lock (_syncRoot)
            {
                _staticStore[fieldKey] = value;
                return value;
            }
        }

        /// <summary>
        /// Registers a newly discovered dynamic field descriptor so the Inspector UI can display and edit it.
        /// </summary>
        public static void RegisterDynamicField(DynamicFieldDescriptor descriptor)
        {
            if (descriptor == null || string.IsNullOrEmpty(descriptor.OwnerTypeFullName))
            {
                return;
            }

            lock (_syncRoot)
            {
                if (!_typeDescriptors.TryGetValue(descriptor.OwnerTypeFullName, out List<DynamicFieldDescriptor> list))
                {
                    list = new List<DynamicFieldDescriptor>();
                    _typeDescriptors[descriptor.OwnerTypeFullName] = list;
                }

                for (int i = 0; i < list.Count; i++)
                {
                    if (string.Equals(list[i].FieldName, descriptor.FieldName, StringComparison.Ordinal))
                    {
                        list[i] = descriptor;
                        return;
                    }
                }

                list.Add(descriptor);
            }
        }

        /// <summary>
        /// Returns all registered dynamic fields for the specified type name, or an empty list if none exist.
        /// </summary>
        public static IReadOnlyList<DynamicFieldDescriptor> GetDynamicFieldsForType(string typeFullName)
        {
            if (string.IsNullOrEmpty(typeFullName))
            {
                return Array.Empty<DynamicFieldDescriptor>();
            }

            lock (_syncRoot)
            {
                if (_typeDescriptors.TryGetValue(typeFullName, out List<DynamicFieldDescriptor> list))
                {
                    return list.ToArray();
                }
            }

            return Array.Empty<DynamicFieldDescriptor>();
        }

        /// <summary>
        /// Clears all static stores and registered field descriptors when exiting Play Mode or performing a Domain Reload.
        /// </summary>
        public static void ClearAll()
        {
            lock (_syncRoot)
            {
                _staticStore.Clear();
                _typeDescriptors.Clear();
            }
        }

        internal static Action<MonoBehaviour, string, float, float, bool> OnScheduleDynamicInvoke;
        internal static Action<MonoBehaviour, string> OnCancelDynamicInvoke;
        internal static Func<MonoBehaviour, string, bool> OnCheckDynamicIsInvoking;

        /// <summary>
        /// Routes MonoBehaviour.Invoke calls so newly added Hot Reload methods execute on time without requiring a Domain Reload.
        /// </summary>
        public static void Invoke(MonoBehaviour instance, string methodName, float time)
        {
            if (instance == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (instance.GetType().GetMethod(methodName, flags) != null)
            {
                instance.Invoke(methodName, time);
                return;
            }

            OnScheduleDynamicInvoke?.Invoke(instance, methodName, time, 0f, false);
        }

        /// <summary>
        /// Routes MonoBehaviour.InvokeRepeating calls so newly added Hot Reload methods repeat without requiring a Domain Reload.
        /// </summary>
        public static void InvokeRepeating(MonoBehaviour instance, string methodName, float time, float repeatRate)
        {
            if (instance == null || string.IsNullOrEmpty(methodName))
            {
                return;
            }

            const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            if (instance.GetType().GetMethod(methodName, flags) != null)
            {
                instance.InvokeRepeating(methodName, time, repeatRate);
                return;
            }

            OnScheduleDynamicInvoke?.Invoke(instance, methodName, time, repeatRate, true);
        }

        /// <summary>
        /// Routes MonoBehaviour.CancelInvoke() calls to cancel both native and dynamic Hot Reload timers.
        /// </summary>
        public static void CancelInvoke(MonoBehaviour instance)
        {
            if (instance == null)
            {
                return;
            }

            instance.CancelInvoke();
            OnCancelDynamicInvoke?.Invoke(instance, null);
        }

        /// <summary>
        /// Routes MonoBehaviour.CancelInvoke(string) calls to cancel both native and dynamic Hot Reload timers.
        /// </summary>
        public static void CancelInvoke(MonoBehaviour instance, string methodName)
        {
            if (instance == null)
            {
                return;
            }

            instance.CancelInvoke(methodName);
            OnCancelDynamicInvoke?.Invoke(instance, methodName);
        }

        /// <summary>
        /// Routes MonoBehaviour.IsInvoking() calls to check both native and dynamic Hot Reload timers.
        /// </summary>
        public static bool IsInvoking(MonoBehaviour instance)
        {
            if (instance == null)
            {
                return false;
            }

            return instance.IsInvoking() || (OnCheckDynamicIsInvoking != null && OnCheckDynamicIsInvoking(instance, null));
        }

        /// <summary>
        /// Routes MonoBehaviour.IsInvoking(string) calls to check both native and dynamic Hot Reload timers.
        /// </summary>
        public static bool IsInvoking(MonoBehaviour instance, string methodName)
        {
            if (instance == null)
            {
                return false;
            }

            return instance.IsInvoking(methodName) || (OnCheckDynamicIsInvoking != null && OnCheckDynamicIsInvoking(instance, methodName));
        }
    }
}
