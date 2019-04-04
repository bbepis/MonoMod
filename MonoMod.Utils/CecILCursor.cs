﻿using System;
using System.Reflection;
using System.Collections.Generic;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Linq;
using MethodBody = Mono.Cecil.Cil.MethodBody;
using OpCodes = Mono.Cecil.Cil.OpCodes;
using OpCode = Mono.Cecil.Cil.OpCode;

namespace MonoMod.Utils {
    public class CecILCursor {

        private static readonly List<object> References = new List<object>();
        private static readonly Dictionary<int, MethodInfo> DelegateInvokers = new Dictionary<int, MethodInfo>();
        public static object GetReference(int id) => References[id];
        public static void SetReference(int id, object obj) => References[id] = obj;

        private readonly List<int> ManagedReferenceIDs = new List<int>();
        public int AddReference(object obj) {
            if (ManagedReferenceIDs.Count == 0) {
                Context.OnDispose += () => {
                    foreach (int id in ManagedReferenceIDs)
                        FreeReference(id);
                };
            }
            ManagedReferenceIDs.Add(References.Count);

            lock (References) {
                References.Add(obj);
                return References.Count - 1;
            }
        }

        public static void FreeReference(int id) {
            References[id] = null;
            if (DelegateInvokers.ContainsKey(id))
                DelegateInvokers.Remove(id);
        }

        private static readonly MethodInfo _GetReference = typeof(CecILCursor).GetMethod("GetReference");

        public CecIL Context { get; }

        private Instruction _Next;
        public Instruction Next {
            get => _Next;
            set {
                if (value != _Next) {
                    _insertAfterLabels = null;
                    _Next = value;
                }
            }
        }
        public Instruction Prev {
            get => Next?.Previous ?? Instrs[Instrs.Count - 1];
            set => Next = value?.Next ?? Instrs[0];
        }
        public Instruction Previous {
            get => Prev;
            set => Prev = value;
        }

        public MethodDefinition Method => Context.Method;
        public ILProcessor IL => Context.IL;

        public MethodBody Body => Context.Body;
        public ModuleDefinition Module => Context.Module;
        public Mono.Collections.Generic.Collection<Instruction> Instrs => Context.Instrs;

        private CecILLabel[] _insertAfterLabels;

        public int Index {
            get {
                int index = Instrs.IndexOf(Next);
                if (index == -1)
                    index = Instrs.Count;
                return index;
            }
            set => Next = value == Instrs.Count ? null : Instrs[value];
        }

        internal CecILCursor(CecIL context, Instruction instr) {
            Context = context;
            Next = instr;
        }

        public CecILCursor(CecILCursor old) {
            Context = old.Context;
            Next = old.Next;
        }

        public CecILCursor Clone()
            => new CecILCursor(this);

        public void Remove() {
            int index = Index;
            IL.Remove(Next);
            Index = index;
        }

        public void GotoLabel(CecILLabel label) {
            Next = label.Target;
        }

        public void MarkLabel(CecILLabel label) {
            label.Target = Next;
            _insertAfterLabels = new [] { label };
        }

        public void MoveAfterLabel() {
            _insertAfterLabels = Context._Labels.Where(l => l.Target == Next).ToArray();
        }

        public void MoveBeforeLabel() {
            _insertAfterLabels = null;
        }

        public IEnumerable<CecILLabel> GetIncomingLabels()
            => Context.GetIncomingLabels(Next);

        #region Misc IL Helpers

        public void GotoNext(params Func<Instruction, bool>[] predicates) {
            if (!TryGotoNext(predicates))
                throw new KeyNotFoundException();
        }
        public bool TryGotoNext(params Func<Instruction, bool>[] predicates) {
            Mono.Collections.Generic.Collection<Instruction> instrs = Instrs;
            for (int i = Index + 1; i + predicates.Length - 1 < instrs.Count; i++) {
                for (int j = 0; j < predicates.Length; j++)
                    if (!(predicates[j]?.Invoke(instrs[i + j]) ?? true))
                        goto Next;

                Index = i;
                return true;

                Next:
                continue;
            }
            return false;
        }

        public void GotoPrev(params Func<Instruction, bool>[] predicates) {
            if (!TryGotoPrev(predicates))
                throw new KeyNotFoundException();
        }
        public bool TryGotoPrev(params Func<Instruction, bool>[] predicates) {
            Mono.Collections.Generic.Collection<Instruction> instrs = Instrs;
            int i = Index - 1;
            int overhang = i + predicates.Length - 1 - instrs.Count;
            if (overhang > 0)
                i -= overhang;

            for (; i > -1; i--) {
                for (int j = 0; j < predicates.Length; j++)
                    if (!(predicates[j]?.Invoke(instrs[i + j]) ?? true))
                        goto Next;

                Index = i;
                return true;

                Next:
                continue;
            }
            return false;
        }

        public void FindNext(out CecILCursor[] cursors, params Func<Instruction, bool>[] predicates) {
            if (!TryFindNext(out cursors, predicates))
                throw new KeyNotFoundException();
        }
        public bool TryFindNext(out CecILCursor[] cursors, params Func<Instruction, bool>[] predicates) {
            cursors = new CecILCursor[predicates.Length];
            Instruction instrOrig = Next;
            Func<Instruction, bool> first = predicates[0];
            while (TryGotoNext(first)) {
                cursors[0] = Clone();
                Instruction instrFirst = Next;
                for (int i = 1; i < predicates.Length; i++) {
                    if (!TryGotoNext(predicates[i]))
                        goto Skip;
                    cursors[i] = Clone();
                }

                Next = instrFirst;
                return true;

                Skip:
                Next = instrFirst;
                continue;
            }
            Next = instrOrig;
            return false;
        }

        public void FindPrev(out CecILCursor[] cursors, params Func<Instruction, bool>[] predicates) {
            if (!TryFindPrev(out cursors, predicates))
                throw new KeyNotFoundException();
        }
        public bool TryFindPrev(out CecILCursor[] cursors, params Func<Instruction, bool>[] predicates) {
            cursors = new CecILCursor[predicates.Length];
            Instruction instrOrig = Next;
            Func<Instruction, bool> last = predicates[predicates.Length - 1];
            while (TryGotoPrev(last)) {
                cursors[predicates.Length - 1] = Clone();
                Instruction instrLast = Next;
                for (int i = predicates.Length - 2; i > -1; i--) {
                    if (!TryGotoPrev(predicates[i]))
                        goto Skip;
                    cursors[i] = Clone();
                }

                Next = instrLast;
                return true;

                Skip:
                Next = instrLast;
                continue;
            }
            Next = instrOrig;
            return false;
        }

        public bool IsBefore(Instruction instr) {
            int indexOther = Instrs.IndexOf(instr);
            if (indexOther == -1)
                indexOther = Instrs.Count;
            return Index <= indexOther;
        }

        public bool IsAfter(Instruction instr) {
            int indexOther = Instrs.IndexOf(instr);
            if (indexOther == -1)
                indexOther = Instrs.Count;
            return indexOther < Index;
        }

        #endregion

        #region Base Create / Emit Helpers

        private CecILCursor _Insert(Instruction instr) {
            Instrs.Insert(Index, instr);
            if (_insertAfterLabels != null) {
                foreach (CecILLabel label in _insertAfterLabels)
                    label.Target = instr;

                _insertAfterLabels = null;
            }
            return new CecILCursor(Context, instr);
        }

        public CecILCursor Emit(OpCode opcode, ParameterDefinition parameter)
            => _Insert(IL.Create(opcode, parameter));
        public CecILCursor Emit(OpCode opcode, VariableDefinition variable)
            => _Insert(IL.Create(opcode, variable));
        public CecILCursor Emit(OpCode opcode, Instruction[] targets)
            => _Insert(IL.Create(opcode, targets));
        public CecILCursor Emit(OpCode opcode, Instruction target)
            => _Insert(IL.Create(opcode, target));
        public CecILCursor Emit(OpCode opcode, double value)
            => _Insert(IL.Create(opcode, value));
        public CecILCursor Emit(OpCode opcode, float value)
            => _Insert(IL.Create(opcode, value));
        public CecILCursor Emit(OpCode opcode, long value)
            => _Insert(IL.Create(opcode, value));
        public CecILCursor Emit(OpCode opcode, sbyte value)
            => _Insert(IL.Create(opcode, value));
        public CecILCursor Emit(OpCode opcode, byte value)
            => _Insert(IL.Create(opcode, value));
        public CecILCursor Emit(OpCode opcode, string value)
            => _Insert(IL.Create(opcode, value));
        public CecILCursor Emit(OpCode opcode, FieldReference field)
            => _Insert(IL.Create(opcode, field));
        public CecILCursor Emit(OpCode opcode, CallSite site)
            => _Insert(IL.Create(opcode, site));
        public CecILCursor Emit(OpCode opcode, TypeReference type)
            => _Insert(IL.Create(opcode, type));
        public CecILCursor Emit(OpCode opcode)
            => _Insert(IL.Create(opcode));
        public CecILCursor Emit(OpCode opcode, int value)
            => _Insert(IL.Create(opcode, value));
        public CecILCursor Emit(OpCode opcode, MethodReference method)
            => _Insert(IL.Create(opcode, method));
        public CecILCursor Emit(OpCode opcode, FieldInfo field)
            => _Insert(IL.Create(opcode, field));
        public CecILCursor Emit(OpCode opcode, MethodBase method)
            => _Insert(IL.Create(opcode, method));
        public CecILCursor Emit(OpCode opcode, Type type)
            => _Insert(IL.Create(opcode, type));
        public CecILCursor Emit(OpCode opcode, object operand)
            => _Insert(IL.Create(opcode, operand));

        #endregion

        #region Reference-oriented Emit Helpers

        /// <summary>
        /// Emit a reference to an arbitrary object. Note that the references "leak" unless you use CecILCursor.FreeReference(id).
        /// </summary>
        public int EmitReference<T>(T obj) {
            Type t = typeof(T);
            int id = AddReference(obj);
            Emit(OpCodes.Ldc_I4, id);
            Emit(OpCodes.Call, _GetReference);
            if (t.GetTypeInfo().IsValueType)
                Emit(OpCodes.Unbox_Any, t);
            return id;
        }

        /// <summary>
        /// Emit a reference to an arbitrary object. Note that the references "leak" unless you use CecILCursor.FreeReference(id).
        /// </summary>
        public void EmitGetReference<T>(int id) {
            Type t = typeof(T);
            Emit(OpCodes.Ldc_I4, id);
            Emit(OpCodes.Call, _GetReference);
            if (t.GetTypeInfo().IsValueType)
                Emit(OpCodes.Unbox_Any, t);
        }

        /// <summary>
        /// Emit an inline delegate reference and invocation. Note that the delegates "leak" unless you use CecILCursor.FreeReference(id).
        /// </summary>
        public int EmitDelegate(Action cb)
            => EmitDelegateInvoke(EmitDelegatePush(cb));

        /// <summary>
        /// Emit an inline delegate reference and invocation. Note that the delegates "leak" unless you use CecILCursor.FreeReference(id).
        /// </summary>
        public int EmitDelegate<T>(T cb) where T : Delegate {
            Instruction instrPrev = Next;
            int id = EmitDelegatePush(cb);

            // Create a DynamicMethod that shifts the stack around a little.

            Type delType = References[id].GetType();
            MethodInfo delInvokeOrig = delType.GetMethod("Invoke");

            ParameterInfo[] args = delInvokeOrig.GetParameters();
            Type[] argTypes = new Type[args.Length + 1];
            for (int i = 0; i < args.Length; i++)
                argTypes[i] = args[i].ParameterType;
            argTypes[args.Length] = delType;

            using (DynamicMethodDefinition dmdInvoke = new DynamicMethodDefinition(
                $"CecIL:Invoke<{delInvokeOrig.DeclaringType.FullName}>?{cb.GetHashCode()}",
                delInvokeOrig.ReturnType, argTypes
            )) {
                ILProcessor il = dmdInvoke.GetILProcessor();

                // Load the delegate reference first.
                il.Emit(OpCodes.Ldarg, args.Length);
                // Load any other arguments on top of that.
                for (int i = 0; i < args.Length; i++)
                    il.Emit(OpCodes.Ldarg, i);
                // Invoke the delegate and return its result.
                il.Emit(OpCodes.Callvirt, delInvokeOrig);
                il.Emit(OpCodes.Ret);

                // Invoke the DynamicMethodDefinition.
                MethodInfo miInvoke = dmdInvoke.Generate();
                DelegateInvokers[id] = miInvoke;
                Emit(OpCodes.Call, miInvoke);
            }

            return id;
        }

        /// <summary>
        /// Emit an inline delegate reference. Note that the delegates "leak" unless you use CecILCursor.FreeReference(id).
        /// </summary>
        public int EmitDelegatePush<T>(T cb) where T : Delegate
            => EmitReference(cb);

        /// <summary>
        /// Emit a delegate invocation.
        /// </summary>
        public int EmitDelegateInvoke(int id) {
            Emit(OpCodes.Callvirt, References[id].GetType().GetMethod("Invoke"));
            return id;
        }

        #endregion

    }
}
