﻿using Mono.Cecil.Cil;
using MonoMod.Utils;
using System;
using System.Reflection;

namespace MonoMod.Cil {
    public static class RuntimeILCursorExtensions {
        /// <summary>
        /// Bind an arbitary object to an ILContext for static retrieval.
        /// </summary>
        /// <typeparam name="T">The type of the object. The combination of typeparam and id provides the unique static reference.</typeparam>
        /// <param name="context">The associated context. The reference will be released when the context is disposed.</param>
        /// <param name="t">The object to store.</param>
        /// <returns>The id to use in combination with the typeparam for object retrieval.</returns>
        public static int AddReference<T>(this ILContext context, T t) {
            int id = ReferenceStore<T>.Store(t);
            context.OnDispose += () => ReferenceStore<T>.Clear(id);
            return id;
        }

        /// <summary>
        /// Bind an arbitary object to an ILContext for static retrieval. See <see cref="AddReference{T}(ILContext, T)"/>
        /// </summary>
        public static int AddReference<T>(this ILCursor cursor, T t) => cursor.Context.AddReference(t);

        /// <summary>
        /// Emits the IL to retrieve a stored reference of type <typeparamref name="T"/> with the given <paramref name="id"/> and place it on the stack.
        /// </summary>
        public static void EmitGetReference<T>(this ILCursor cursor, int id) {
            cursor.Emit(OpCodes.Ldc_I4, id);
            cursor.Emit(OpCodes.Call, ReferenceStore<T>.GetMethod);
        }

        /// <summary>
        /// Store an object in the reference store, and emit the IL to retrieve it and place it on the stack.
        /// </summary>
        public static int EmitReference<T>(this ILCursor cursor, T t) {
            int id = AddReference(cursor, t);
            cursor.EmitGetReference<T>(id);
            return id;
        }

        /// <summary>
        /// Emit the IL to invoke a delegate as if it were a method. Stack behaviour matches OpCodes.Call
        /// </summary>
        public static void EmitDelegate<T>(this ILCursor cursor, T cb) where T : Delegate {
            cursor.EmitReference(cb);
            if (cb.TryCastDelegate(out Action _)) {
                // optimisation for no-arg delegates
                cursor.Emit(OpCodes.Callvirt, typeof(T).GetMethod("Invoke"));
                return;
            }

            // As the delegate reference is now on the top of the stack, and Invoke requires the delegate to be on the bottom,
            // Emit a dynamic method which stores the stack into params and then Invokes the delegate

            Type delType = typeof(T);
            MethodInfo delInvoke = delType.GetMethod("Invoke");

            ParameterInfo[] args = delInvoke.GetParameters();
            Type[] argTypes = new Type[args.Length + 1];
            for (int i = 0; i < args.Length; i++)
                argTypes[i] = args[i].ParameterType;
            argTypes[args.Length] = delType;

            using (DynamicMethodDefinition dmdInvoke = new DynamicMethodDefinition(
                $"MMIL:Invoke<{delInvoke.DeclaringType.FullName}>?{cb.GetHashCode()}",
                delInvoke.ReturnType, argTypes
            )) {
                ILProcessor il = dmdInvoke.GetILProcessor();

                // Load the delegate reference first.
                il.Emit(OpCodes.Ldarg, args.Length);
                // Load any other arguments on top of that.
                for (int i = 0; i < args.Length; i++)
                    il.Emit(OpCodes.Ldarg, i);
                // Invoke the delegate and return its result.
                il.Emit(OpCodes.Callvirt, delInvoke);
                il.Emit(OpCodes.Ret);

                // Invoke the DynamicMethodDefinition.
                MethodInfo miInvoke = dmdInvoke.Generate();
                cursor.AddReference(miInvoke);//pin the method so it doesn't get garbage collected until the context does
                cursor.Emit(OpCodes.Call, miInvoke);
            }
        }
    }

    public static class ReferenceStore<T> {
        private static T[] array = new T[4];
        private static int count;

        public static T Get(int id) => array[id];
        public static readonly MethodInfo GetMethod = typeof(ReferenceStore<T>).GetMethod("Get");

        private static object _storeLock = new object();
        public static int Store(T t) {
            lock (_storeLock) {
                if (count == array.Length) {
                    var newarray = new T[array.Length * 2];
                    Array.Copy(array, newarray, array.Length);
                    array = newarray;
                }
                array[count] = t;
                return count++;
            }
        }

        public static void Clear(int id) {
            lock(_storeLock)
                array[id] = default;
        }
    }
}
