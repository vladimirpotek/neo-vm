using Neo.VM.Types;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using Array = System.Array;
using Buffer = Neo.VM.Types.Buffer;
using VMArray = Neo.VM.Types.Array;

namespace Neo.VM
{
    public class ExecutionEngine : IDisposable
    {
        private VMState state = VMState.BREAK;

        #region Limits Variables

        /// <summary>
        /// Max value for SHL and SHR
        /// </summary>
        public virtual int MaxShift => 256;

        /// <summary>
        /// Set the max Stack Size
        /// </summary>
        public virtual uint MaxStackSize => 2 * 1024;

        /// <summary>
        /// Set Max Item Size
        /// </summary>
        public virtual uint MaxItemSize => 1024 * 1024;

        /// <summary>
        /// Set Max Invocation Stack Size
        /// </summary>
        public virtual uint MaxInvocationStackSize => 1024;

        #endregion

        public ReferenceCounter ReferenceCounter { get; }
        public Stack<ExecutionContext> InvocationStack { get; } = new Stack<ExecutionContext>();
        public ExecutionContext CurrentContext { get; private set; }
        public ExecutionContext EntryContext { get; private set; }
        public EvaluationStack ResultStack { get; }
        public StackItem UncaughtException { get; private set; }

        public VMState State
        {
            get
            {
                return state;
            }
            internal protected set
            {
                if (state != value)
                {
                    state = value;
                    OnStateChanged();
                }
            }
        }

        public ExecutionEngine() : this(new ReferenceCounter())
        {
        }

        protected ExecutionEngine(ReferenceCounter referenceCounter)
        {
            this.ReferenceCounter = referenceCounter;
            this.ResultStack = new EvaluationStack(referenceCounter);
        }

        #region Limits

        /// <summary>
        /// Check if the is possible to overflow the MaxItemSize
        /// </summary>
        /// <param name="length">Length</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertMaxItemSize(int length)
        {
            if (length < 0 || length > MaxItemSize)
            {
                throw new InvalidOperationException($"MaxItemSize exceed: {length}");
            }
        }

        /// <summary>
        /// Check if the number is allowed from SHL and SHR
        /// </summary>
        /// <param name="shift">Shift</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void AssertShift(int shift)
        {
            if (shift > MaxShift || shift < 0)
            {
                throw new InvalidOperationException($"Invalid shift value: {shift}");
            }
        }

        #endregion

        protected virtual void ContextUnloaded(ExecutionContext context)
        {
            if (InvocationStack.Count == 0)
            {
                CurrentContext = null;
                EntryContext = null;
            }
            else
            {
                CurrentContext = InvocationStack.Peek();
            }
            if (context.StaticFields != null && context.StaticFields != CurrentContext?.StaticFields)
            {
                context.StaticFields.ClearReferences();
            }
            context.LocalVariables?.ClearReferences();
            context.Arguments?.ClearReferences();
        }

        public virtual void Dispose()
        {
            InvocationStack.Clear();
        }

        public VMState Execute()
        {
            if (State == VMState.BREAK)
                State = VMState.NONE;
            while (State != VMState.HALT && State != VMState.FAULT)
                ExecuteNext();
            return State;
        }

        private void ExecuteInstruction(Instruction instruction)
        {
            ExecutionContext context = CurrentContext;
            switch (instruction.OpCode)
            {
                //Push
                case OpCode.PUSHINT8:
                case OpCode.PUSHINT16:
                case OpCode.PUSHINT32:
                case OpCode.PUSHINT64:
                case OpCode.PUSHINT128:
                case OpCode.PUSHINT256:
                    {
                        Push(new BigInteger(instruction.Operand.Span));
                        break;
                    }
                case OpCode.PUSHA:
                    {
                        int position = checked(context.InstructionPointer + instruction.TokenI32);
                        if (position < 0 || position > context.Script.Length)
                            throw new InvalidOperationException($"Bad pointer address: {position}");
                        Push(new Pointer(context.Script, position));
                        break;
                    }
                case OpCode.PUSHNULL:
                    {
                        Push(StackItem.Null);
                        break;
                    }
                case OpCode.PUSHDATA1:
                case OpCode.PUSHDATA2:
                case OpCode.PUSHDATA4:
                    {
                        AssertMaxItemSize(instruction.Operand.Length);
                        Push(instruction.Operand);
                        break;
                    }
                case OpCode.PUSHM1:
                case OpCode.PUSH0:
                case OpCode.PUSH1:
                case OpCode.PUSH2:
                case OpCode.PUSH3:
                case OpCode.PUSH4:
                case OpCode.PUSH5:
                case OpCode.PUSH6:
                case OpCode.PUSH7:
                case OpCode.PUSH8:
                case OpCode.PUSH9:
                case OpCode.PUSH10:
                case OpCode.PUSH11:
                case OpCode.PUSH12:
                case OpCode.PUSH13:
                case OpCode.PUSH14:
                case OpCode.PUSH15:
                case OpCode.PUSH16:
                    {
                        Push((int)instruction.OpCode - (int)OpCode.PUSH0);
                        break;
                    }

                // Control
                case OpCode.NOP: break;
                case OpCode.JMP:
                    {
                        ExecuteJump(true, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMP_L:
                    {
                        ExecuteJump(true, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPIF:
                    {
                        var x = Pop().GetBoolean();
                        ExecuteJump(x, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPIF_L:
                    {
                        var x = Pop().GetBoolean();
                        ExecuteJump(x, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPIFNOT:
                    {
                        var x = Pop().GetBoolean();
                        ExecuteJump(!x, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPIFNOT_L:
                    {
                        var x = Pop().GetBoolean();
                        ExecuteJump(!x, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPEQ:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 == x2, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPEQ_L:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 == x2, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPNE:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 != x2, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPNE_L:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 != x2, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPGT:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 > x2, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPGT_L:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 > x2, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPGE:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 >= x2, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPGE_L:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 >= x2, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPLT:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 < x2, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPLT_L:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 < x2, instruction.TokenI32);
                        return;
                    }
                case OpCode.JMPLE:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 <= x2, instruction.TokenI8);
                        return;
                    }
                case OpCode.JMPLE_L:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        ExecuteJump(x1 <= x2, instruction.TokenI32);
                        return;
                    }
                case OpCode.CALL:
                    {
                        LoadClonedContext(checked(context.InstructionPointer + instruction.TokenI8));
                        break;
                    }
                case OpCode.CALL_L:
                    {
                        LoadClonedContext(checked(context.InstructionPointer + instruction.TokenI32));
                        break;
                    }
                case OpCode.CALLA:
                    {
                        var x = Pop<Pointer>();
                        if (!x.Script.Equals(context.Script))
                            throw new InvalidOperationException("Pointers can't be shared between scripts");
                        LoadClonedContext(x.Position);
                        break;
                    }
                case OpCode.ABORT:
                    {
                        throw new Exception($"{OpCode.ABORT} is executed.");
                    }
                case OpCode.ASSERT:
                    {
                        var x = Pop().GetBoolean();
                        if (!x)
                            throw new Exception($"{OpCode.ASSERT} is executed with false result.");
                        break;
                    }
                case OpCode.THROW:
                    {
                        Throw(Pop());
                        return;
                    }
                case OpCode.TRY:
                    {
                        int catchOffset = instruction.TokenI8;
                        int finallyOffset = instruction.TokenI8_1;
                        ExecuteTry(catchOffset, finallyOffset);
                        break;
                    }
                case OpCode.TRY_L:
                    {
                        int catchOffset = instruction.TokenI32;
                        int finallyOffset = instruction.TokenI32_1;
                        ExecuteTry(catchOffset, finallyOffset);
                        break;
                    }
                case OpCode.ENDTRY:
                    {
                        int endOffset = instruction.TokenI8;
                        ExecuteEndTry(endOffset);
                        return;
                    }
                case OpCode.ENDTRY_L:
                    {
                        int endOffset = instruction.TokenI32;
                        ExecuteEndTry(endOffset);
                        return;
                    }
                case OpCode.ENDFINALLY:
                    {
                        if (context.TryStack is null)
                            throw new InvalidOperationException($"The corresponding TRY block cannot be found.");
                        if (!context.TryStack.TryPop(out ExceptionHandlingContext currentTry))
                            throw new InvalidOperationException($"The corresponding TRY block cannot be found.");

                        if (UncaughtException is null)
                            context.InstructionPointer = currentTry.EndPointer;
                        else
                            HandleException();
                        return;
                    }
                case OpCode.RET:
                    {
                        ExecutionContext context_pop = InvocationStack.Pop();
                        EvaluationStack stack_eval = InvocationStack.Count == 0 ? ResultStack : InvocationStack.Peek().EvaluationStack;
                        if (context_pop.EvaluationStack != stack_eval)
                            context_pop.EvaluationStack.CopyTo(stack_eval);
                        if (InvocationStack.Count == 0)
                            State = VMState.HALT;
                        ContextUnloaded(context_pop);
                        return;
                    }
                case OpCode.SYSCALL:
                    {
                        OnSysCall(instruction.TokenU32);
                        break;
                    }

                // Stack ops
                case OpCode.DEPTH:
                    {
                        Push(context.EvaluationStack.Count);
                        break;
                    }
                case OpCode.DROP:
                    {
                        Pop();
                        break;
                    }
                case OpCode.NIP:
                    {
                        context.EvaluationStack.Remove<StackItem>(1);
                        break;
                    }
                case OpCode.XDROP:
                    {
                        int n = (int)Pop().GetInteger();
                        if (n < 0)
                            throw new InvalidOperationException($"The negative value {n} is invalid for OpCode.{instruction.OpCode}.");
                        context.EvaluationStack.Remove<StackItem>(n);
                        break;
                    }
                case OpCode.CLEAR:
                    {
                        context.EvaluationStack.Clear();
                        break;
                    }
                case OpCode.DUP:
                    {
                        Push(Peek());
                        break;
                    }
                case OpCode.OVER:
                    {
                        Push(Peek(1));
                        break;
                    }
                case OpCode.PICK:
                    {
                        int n = (int)Pop().GetInteger();
                        if (n < 0)
                            throw new InvalidOperationException($"The negative value {n} is invalid for OpCode.{instruction.OpCode}.");
                        Push(Peek(n));
                        break;
                    }
                case OpCode.TUCK:
                    {
                        context.EvaluationStack.Insert(2, Peek());
                        break;
                    }
                case OpCode.SWAP:
                    {
                        var x = context.EvaluationStack.Remove<StackItem>(1);
                        Push(x);
                        break;
                    }
                case OpCode.ROT:
                    {
                        var x = context.EvaluationStack.Remove<StackItem>(2);
                        Push(x);
                        break;
                    }
                case OpCode.ROLL:
                    {
                        int n = (int)Pop().GetInteger();
                        if (n < 0)
                            throw new InvalidOperationException($"The negative value {n} is invalid for OpCode.{instruction.OpCode}.");
                        if (n == 0) break;
                        var x = context.EvaluationStack.Remove<StackItem>(n);
                        Push(x);
                        break;
                    }
                case OpCode.REVERSE3:
                    {
                        context.EvaluationStack.Reverse(3);
                        break;
                    }
                case OpCode.REVERSE4:
                    {
                        context.EvaluationStack.Reverse(4);
                        break;
                    }
                case OpCode.REVERSEN:
                    {
                        int n = (int)Pop().GetInteger();
                        context.EvaluationStack.Reverse(n);
                        break;
                    }

                //Slot
                case OpCode.INITSSLOT:
                    {
                        if (context.StaticFields != null)
                            throw new InvalidOperationException($"{instruction.OpCode} cannot be executed twice.");
                        if (instruction.TokenU8 == 0)
                            throw new InvalidOperationException($"The operand {instruction.TokenU8} is invalid for OpCode.{instruction.OpCode}.");
                        context.StaticFields = new Slot(instruction.TokenU8, ReferenceCounter);
                        break;
                    }
                case OpCode.INITSLOT:
                    {
                        if (context.LocalVariables != null || context.Arguments != null)
                            throw new InvalidOperationException($"{instruction.OpCode} cannot be executed twice.");
                        if (instruction.TokenU16 == 0)
                            throw new InvalidOperationException($"The operand {instruction.TokenU16} is invalid for OpCode.{instruction.OpCode}.");
                        if (instruction.TokenU8 > 0)
                        {
                            context.LocalVariables = new Slot(instruction.TokenU8, ReferenceCounter);
                        }
                        if (instruction.TokenU8_1 > 0)
                        {
                            StackItem[] items = new StackItem[instruction.TokenU8_1];
                            for (int i = 0; i < instruction.TokenU8_1; i++)
                            {
                                items[i] = Pop();
                            }
                            context.Arguments = new Slot(items, ReferenceCounter);
                        }
                        break;
                    }
                case OpCode.LDSFLD0:
                case OpCode.LDSFLD1:
                case OpCode.LDSFLD2:
                case OpCode.LDSFLD3:
                case OpCode.LDSFLD4:
                case OpCode.LDSFLD5:
                case OpCode.LDSFLD6:
                    {
                        ExecuteLoadFromSlot(context.StaticFields, instruction.OpCode - OpCode.LDSFLD0);
                        break;
                    }
                case OpCode.LDSFLD:
                    {
                        ExecuteLoadFromSlot(context.StaticFields, instruction.TokenU8);
                        break;
                    }
                case OpCode.STSFLD0:
                case OpCode.STSFLD1:
                case OpCode.STSFLD2:
                case OpCode.STSFLD3:
                case OpCode.STSFLD4:
                case OpCode.STSFLD5:
                case OpCode.STSFLD6:
                    {
                        ExecuteStoreToSlot(context.StaticFields, instruction.OpCode - OpCode.STSFLD0);
                        break;
                    }
                case OpCode.STSFLD:
                    {
                        ExecuteStoreToSlot(context.StaticFields, instruction.TokenU8);
                        break;
                    }
                case OpCode.LDLOC0:
                case OpCode.LDLOC1:
                case OpCode.LDLOC2:
                case OpCode.LDLOC3:
                case OpCode.LDLOC4:
                case OpCode.LDLOC5:
                case OpCode.LDLOC6:
                    {
                        ExecuteLoadFromSlot(context.LocalVariables, instruction.OpCode - OpCode.LDLOC0);
                        break;
                    }
                case OpCode.LDLOC:
                    {
                        ExecuteLoadFromSlot(context.LocalVariables, instruction.TokenU8);
                        break;
                    }
                case OpCode.STLOC0:
                case OpCode.STLOC1:
                case OpCode.STLOC2:
                case OpCode.STLOC3:
                case OpCode.STLOC4:
                case OpCode.STLOC5:
                case OpCode.STLOC6:
                    {
                        ExecuteStoreToSlot(context.LocalVariables, instruction.OpCode - OpCode.STLOC0);
                        break;
                    }
                case OpCode.STLOC:
                    {
                        ExecuteStoreToSlot(context.LocalVariables, instruction.TokenU8);
                        break;
                    }
                case OpCode.LDARG0:
                case OpCode.LDARG1:
                case OpCode.LDARG2:
                case OpCode.LDARG3:
                case OpCode.LDARG4:
                case OpCode.LDARG5:
                case OpCode.LDARG6:
                    {
                        ExecuteLoadFromSlot(context.Arguments, instruction.OpCode - OpCode.LDARG0);
                        break;
                    }
                case OpCode.LDARG:
                    {
                        ExecuteLoadFromSlot(context.Arguments, instruction.TokenU8);
                        break;
                    }
                case OpCode.STARG0:
                case OpCode.STARG1:
                case OpCode.STARG2:
                case OpCode.STARG3:
                case OpCode.STARG4:
                case OpCode.STARG5:
                case OpCode.STARG6:
                    {
                        ExecuteStoreToSlot(context.Arguments, instruction.OpCode - OpCode.STARG0);
                        break;
                    }
                case OpCode.STARG:
                    {
                        ExecuteStoreToSlot(context.Arguments, instruction.TokenU8);
                        break;
                    }

                // Splice
                case OpCode.NEWBUFFER:
                    {
                        int length = (int)Pop().GetInteger();
                        AssertMaxItemSize(length);
                        Push(new Buffer(length));
                        break;
                    }
                case OpCode.MEMCPY:
                    {
                        int count = (int)Pop().GetInteger();
                        if (count < 0)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        int si = (int)Pop().GetInteger();
                        if (si < 0)
                            throw new InvalidOperationException($"The value {si} is out of range.");
                        ReadOnlySpan<byte> src = Pop().GetSpan();
                        if (checked(si + count) > src.Length)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        int di = (int)Pop().GetInteger();
                        if (di < 0)
                            throw new InvalidOperationException($"The value {di} is out of range.");
                        Buffer dst = Pop<Buffer>();
                        if (checked(di + count) > dst.Size)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        src.Slice(si, count).CopyTo(dst.InnerBuffer.AsSpan(di));
                        break;
                    }
                case OpCode.CAT:
                    {
                        var x2 = Pop().GetSpan();
                        var x1 = Pop().GetSpan();
                        int length = x1.Length + x2.Length;
                        AssertMaxItemSize(length);
                        Buffer result = new Buffer(length);
                        x1.CopyTo(result.InnerBuffer);
                        x2.CopyTo(result.InnerBuffer.AsSpan(x1.Length));
                        Push(result);
                        break;
                    }
                case OpCode.SUBSTR:
                    {
                        int count = (int)Pop().GetInteger();
                        if (count < 0)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        int index = (int)Pop().GetInteger();
                        if (index < 0)
                            throw new InvalidOperationException($"The value {index} is out of range.");
                        var x = Pop().GetSpan();
                        if (index + count > x.Length)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        Buffer result = new Buffer(count);
                        x.Slice(index, count).CopyTo(result.InnerBuffer);
                        Push(result);
                        break;
                    }
                case OpCode.LEFT:
                    {
                        int count = (int)Pop().GetInteger();
                        if (count < 0)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        var x = Pop().GetSpan();
                        if (count > x.Length)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        Buffer result = new Buffer(count);
                        x[..count].CopyTo(result.InnerBuffer);
                        Push(result);
                        break;
                    }
                case OpCode.RIGHT:
                    {
                        int count = (int)Pop().GetInteger();
                        if (count < 0)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        var x = Pop().GetSpan();
                        if (count > x.Length)
                            throw new InvalidOperationException($"The value {count} is out of range.");
                        Buffer result = new Buffer(count);
                        x[^count..^0].CopyTo(result.InnerBuffer);
                        Push(result);
                        break;
                    }

                // Bitwise logic
                case OpCode.INVERT:
                    {
                        var x = Pop().GetInteger();
                        Push(~x);
                        break;
                    }
                case OpCode.AND:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 & x2);
                        break;
                    }
                case OpCode.OR:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 | x2);
                        break;
                    }
                case OpCode.XOR:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 ^ x2);
                        break;
                    }
                case OpCode.EQUAL:
                    {
                        StackItem x2 = Pop();
                        StackItem x1 = Pop();
                        Push(x1.Equals(x2));
                        break;
                    }
                case OpCode.NOTEQUAL:
                    {
                        StackItem x2 = Pop();
                        StackItem x1 = Pop();
                        Push(!x1.Equals(x2));
                        break;
                    }

                // Numeric
                case OpCode.SIGN:
                    {
                        var x = Pop().GetInteger();
                        Push(x.Sign);
                        break;
                    }
                case OpCode.ABS:
                    {
                        var x = Pop().GetInteger();
                        Push(BigInteger.Abs(x));
                        break;
                    }
                case OpCode.NEGATE:
                    {
                        var x = Pop().GetInteger();
                        Push(-x);
                        break;
                    }
                case OpCode.INC:
                    {
                        var x = Pop().GetInteger();
                        Push(x + 1);
                        break;
                    }
                case OpCode.DEC:
                    {
                        var x = Pop().GetInteger();
                        Push(x - 1);
                        break;
                    }
                case OpCode.ADD:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 + x2);
                        break;
                    }
                case OpCode.SUB:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 - x2);
                        break;
                    }
                case OpCode.MUL:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 * x2);
                        break;
                    }
                case OpCode.DIV:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 / x2);
                        break;
                    }
                case OpCode.MOD:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 % x2);
                        break;
                    }
                case OpCode.SHL:
                    {
                        int shift = (int)Pop().GetInteger();
                        AssertShift(shift);
                        if (shift == 0) break;
                        var x = Pop().GetInteger();
                        Push(x << shift);
                        break;
                    }
                case OpCode.SHR:
                    {
                        int shift = (int)Pop().GetInteger();
                        AssertShift(shift);
                        if (shift == 0) break;
                        var x = Pop().GetInteger();
                        Push(x >> shift);
                        break;
                    }
                case OpCode.NOT:
                    {
                        var x = Pop().GetBoolean();
                        Push(!x);
                        break;
                    }
                case OpCode.BOOLAND:
                    {
                        var x2 = Pop().GetBoolean();
                        var x1 = Pop().GetBoolean();
                        Push(x1 && x2);
                        break;
                    }
                case OpCode.BOOLOR:
                    {
                        var x2 = Pop().GetBoolean();
                        var x1 = Pop().GetBoolean();
                        Push(x1 || x2);
                        break;
                    }
                case OpCode.NZ:
                    {
                        var x = Pop().GetInteger();
                        Push(!x.IsZero);
                        break;
                    }
                case OpCode.NUMEQUAL:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 == x2);
                        break;
                    }
                case OpCode.NUMNOTEQUAL:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 != x2);
                        break;
                    }
                case OpCode.LT:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 < x2);
                        break;
                    }
                case OpCode.LE:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 <= x2);
                        break;
                    }
                case OpCode.GT:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 > x2);
                        break;
                    }
                case OpCode.GE:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(x1 >= x2);
                        break;
                    }
                case OpCode.MIN:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(BigInteger.Min(x1, x2));
                        break;
                    }
                case OpCode.MAX:
                    {
                        var x2 = Pop().GetInteger();
                        var x1 = Pop().GetInteger();
                        Push(BigInteger.Max(x1, x2));
                        break;
                    }
                case OpCode.WITHIN:
                    {
                        BigInteger b = Pop().GetInteger();
                        BigInteger a = Pop().GetInteger();
                        var x = Pop().GetInteger();
                        Push(a <= x && x < b);
                        break;
                    }

                // Compound-type
                case OpCode.PACK:
                    {
                        int size = (int)Pop().GetInteger();
                        if (size < 0 || size > context.EvaluationStack.Count)
                            throw new InvalidOperationException($"The value {size} is out of range.");
                        VMArray array = new VMArray(ReferenceCounter);
                        for (int i = 0; i < size; i++)
                        {
                            StackItem item = Pop();
                            array.Add(item);
                        }
                        Push(array);
                        break;
                    }
                case OpCode.UNPACK:
                    {
                        VMArray array = Pop<VMArray>();
                        for (int i = array.Count - 1; i >= 0; i--)
                            Push(array[i]);
                        Push(array.Count);
                        break;
                    }
                case OpCode.NEWARRAY0:
                    {
                        Push(new VMArray(ReferenceCounter));
                        break;
                    }
                case OpCode.NEWARRAY:
                case OpCode.NEWARRAY_T:
                    {
                        int n = (int)Pop().GetInteger();
                        if (n < 0 || n > MaxStackSize)
                            throw new InvalidOperationException($"MaxStackSize exceed: {n}");
                        StackItem item;
                        if (instruction.OpCode == OpCode.NEWARRAY_T)
                        {
                            StackItemType type = (StackItemType)instruction.TokenU8;
                            if (!Enum.IsDefined(typeof(StackItemType), type))
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {instruction.TokenU8}");
                            item = instruction.TokenU8 switch
                            {
                                (byte)StackItemType.Boolean => StackItem.False,
                                (byte)StackItemType.Integer => Integer.Zero,
                                (byte)StackItemType.ByteString => ByteString.Empty,
                                _ => StackItem.Null
                            };
                        }
                        else
                        {
                            item = StackItem.Null;
                        }
                        Push(new VMArray(ReferenceCounter, Enumerable.Repeat(item, n)));
                        break;
                    }
                case OpCode.NEWSTRUCT0:
                    {
                        Push(new Struct(ReferenceCounter));
                        break;
                    }
                case OpCode.NEWSTRUCT:
                    {
                        int n = (int)Pop().GetInteger();
                        if (n < 0 || n > MaxStackSize)
                            throw new InvalidOperationException($"MaxStackSize exceed: {n}");
                        Struct result = new Struct(ReferenceCounter);
                        for (var i = 0; i < n; i++)
                            result.Add(StackItem.Null);
                        Push(result);
                        break;
                    }
                case OpCode.NEWMAP:
                    {
                        Push(new Map(ReferenceCounter));
                        break;
                    }
                case OpCode.SIZE:
                    {
                        var x = Pop();
                        switch (x)
                        {
                            case CompoundType compound:
                                Push(compound.Count);
                                break;
                            case PrimitiveType primitive:
                                Push(primitive.Size);
                                break;
                            case Buffer buffer:
                                Push(buffer.Size);
                                break;
                            default:
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {x.Type}");
                        }
                        break;
                    }
                case OpCode.HASKEY:
                    {
                        PrimitiveType key = Pop<PrimitiveType>();
                        var x = Pop();
                        switch (x)
                        {
                            case VMArray array:
                                {
                                    int index = key.ToInt32();
                                    if (index < 0)
                                        throw new InvalidOperationException($"The negative value {index} is invalid for OpCode.{instruction.OpCode}.");
                                    Push(index < array.Count);
                                    break;
                                }
                            case Map map:
                                {
                                    Push(map.ContainsKey(key));
                                    break;
                                }
                            case Buffer buffer:
                                {
                                    int index = key.ToInt32();
                                    if (index < 0)
                                        throw new InvalidOperationException($"The negative value {index} is invalid for OpCode.{instruction.OpCode}.");
                                    Push(index < buffer.Size);
                                    break;
                                }
                            case ByteString array:
                                {
                                    int index = key.ToInt32();
                                    if (index < 0)
                                        throw new InvalidOperationException($"The negative value {index} is invalid for OpCode.{instruction.OpCode}.");
                                    Push(index < array.Size);
                                    break;
                                }
                            default:
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {x.Type}");
                        }
                        break;
                    }
                case OpCode.KEYS:
                    {
                        Map map = Pop<Map>();
                        Push(new VMArray(ReferenceCounter, map.Keys));
                        break;
                    }
                case OpCode.VALUES:
                    {
                        IEnumerable<StackItem> values;
                        var x = Pop();
                        switch (x)
                        {
                            case VMArray array:
                                values = array;
                                break;
                            case Map map:
                                values = map.Values;
                                break;
                            default:
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {x.Type}");
                        }
                        VMArray newArray = new VMArray(ReferenceCounter);
                        foreach (StackItem item in values)
                            if (item is Struct s)
                                newArray.Add(s.Clone());
                            else
                                newArray.Add(item);
                        Push(newArray);
                        break;
                    }
                case OpCode.PICKITEM:
                    {
                        PrimitiveType key = Pop<PrimitiveType>();
                        var x = Pop();
                        switch (x)
                        {
                            case VMArray array:
                                {
                                    int index = key.ToInt32();
                                    if (index < 0 || index >= array.Count)
                                        throw new InvalidOperationException($"The value {index} is out of range.");
                                    Push(array[index]);
                                    break;
                                }
                            case Map map:
                                {
                                    if (!map.TryGetValue(key, out StackItem value))
                                        throw new InvalidOperationException($"Key not found in {nameof(Map)}");
                                    Push(value);
                                    break;
                                }
                            case PrimitiveType primitive:
                                {
                                    ReadOnlySpan<byte> byteArray = primitive.GetSpan();
                                    int index = key.ToInt32();
                                    if (index < 0 || index >= byteArray.Length)
                                        throw new InvalidOperationException($"The value {index} is out of range.");
                                    Push((BigInteger)byteArray[index]);
                                    break;
                                }
                            case Buffer buffer:
                                {
                                    int index = key.ToInt32();
                                    if (index < 0 || index >= buffer.Size)
                                        throw new InvalidOperationException($"The value {index} is out of range.");
                                    Push((BigInteger)buffer.InnerBuffer[index]);
                                    break;
                                }
                            default:
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {x.Type}");
                        }
                        break;
                    }
                case OpCode.APPEND:
                    {
                        StackItem newItem = Pop();
                        VMArray array = Pop<VMArray>();
                        if (newItem is Struct s) newItem = s.Clone();
                        array.Add(newItem);
                        break;
                    }
                case OpCode.SETITEM:
                    {
                        StackItem value = Pop();
                        if (value is Struct s) value = s.Clone();
                        PrimitiveType key = Pop<PrimitiveType>();
                        var x = Pop();
                        switch (x)
                        {
                            case VMArray array:
                                {
                                    int index = key.ToInt32();
                                    if (index < 0 || index >= array.Count)
                                        throw new InvalidOperationException($"The value {index} is out of range.");
                                    array[index] = value;
                                    break;
                                }
                            case Map map:
                                {
                                    map[key] = value;
                                    break;
                                }
                            case Buffer buffer:
                                {
                                    int index = key.ToInt32();
                                    if (index < 0 || index >= buffer.Size)
                                        throw new InvalidOperationException($"The value {index} is out of range.");
                                    if (!(value is PrimitiveType p))
                                        throw new InvalidOperationException($"Value must be a primitive type in {instruction.OpCode}");
                                    int b = p.ToInt32();
                                    if (b < sbyte.MinValue || b > byte.MaxValue)
                                        throw new InvalidOperationException($"Overflow in {instruction.OpCode}, {b} is not a byte type.");
                                    buffer.InnerBuffer[index] = (byte)b;
                                    break;
                                }
                            default:
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {x.Type}");
                        }
                        break;
                    }
                case OpCode.REVERSEITEMS:
                    {
                        var x = Pop();
                        switch (x)
                        {
                            case VMArray array:
                                array.Reverse();
                                break;
                            case Buffer buffer:
                                Array.Reverse(buffer.InnerBuffer);
                                break;
                            default:
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {x.Type}");
                        }
                        break;
                    }
                case OpCode.REMOVE:
                    {
                        PrimitiveType key = Pop<PrimitiveType>();
                        var x = Pop();
                        switch (x)
                        {
                            case VMArray array:
                                int index = key.ToInt32();
                                if (index < 0 || index >= array.Count)
                                    throw new InvalidOperationException($"The value {index} is out of range.");
                                array.RemoveAt(index);
                                break;
                            case Map map:
                                map.Remove(key);
                                break;
                            default:
                                throw new InvalidOperationException($"Invalid type for {instruction.OpCode}: {x.Type}");
                        }
                        break;
                    }
                case OpCode.CLEARITEMS:
                    {
                        CompoundType x = Pop<CompoundType>();
                        x.Clear();
                        break;
                    }

                //Types
                case OpCode.ISNULL:
                    {
                        var x = Pop();
                        Push(x.IsNull);
                        break;
                    }
                case OpCode.ISTYPE:
                    {
                        var x = Pop();
                        StackItemType type = (StackItemType)instruction.TokenU8;
                        if (type == StackItemType.Any || !Enum.IsDefined(typeof(StackItemType), type))
                            throw new InvalidOperationException($"Invalid type: {type}");
                        Push(x.Type == type);
                        break;
                    }
                case OpCode.CONVERT:
                    {
                        var x = Pop();
                        Push(x.ConvertTo((StackItemType)instruction.TokenU8));
                        break;
                    }

                default: throw new InvalidOperationException($"Opcode {instruction.OpCode} is undefined.");
            }
            context.MoveNext();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteEndTry(int endOffset)
        {
            if (CurrentContext.TryStack is null)
                throw new InvalidOperationException($"The corresponding TRY block cannot be found.");
            if (!CurrentContext.TryStack.TryPeek(out ExceptionHandlingContext currentTry))
                throw new InvalidOperationException($"The corresponding TRY block cannot be found.");
            if (currentTry.State == ExceptionHandlingState.Finally)
                throw new InvalidOperationException($"The opcode {OpCode.ENDTRY} can't be executed in a FINALLY block.");

            int endPointer = checked(CurrentContext.InstructionPointer + endOffset);
            if (currentTry.HasFinally)
            {
                currentTry.State = ExceptionHandlingState.Finally;
                currentTry.EndPointer = endPointer;
                CurrentContext.InstructionPointer = currentTry.FinallyPointer;
            }
            else
            {
                CurrentContext.TryStack.Pop();
                CurrentContext.InstructionPointer = endPointer;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteJump(bool condition, int offset)
        {
            offset = checked(CurrentContext.InstructionPointer + offset);

            if (offset < 0 || offset > CurrentContext.Script.Length)
                throw new InvalidOperationException($"Jump out of range for offset: {offset}");
            if (condition)
                CurrentContext.InstructionPointer = offset;
            else
                CurrentContext.MoveNext();
        }

        private void ExecuteLoadFromSlot(Slot slot, int index)
        {
            if (slot is null)
                throw new InvalidOperationException("Slot has not been initialized.");
            if (index < 0 || index >= slot.Count)
                throw new InvalidOperationException($"Index out of range when loading from slot: {index}");
            Push(slot[index]);
        }

        internal protected void ExecuteNext()
        {
            if (InvocationStack.Count == 0)
            {
                State = VMState.HALT;
            }
            else
            {
                try
                {
                    PreExecuteInstruction();
                    Instruction instruction = CurrentContext.CurrentInstruction;
                    ExecuteInstruction(instruction);
                    PostExecuteInstruction(instruction);
                }
                catch (Exception e)
                {
                    OnFault(e);
                }
            }
        }

        private void ExecuteStoreToSlot(Slot slot, int index)
        {
            if (slot is null)
                throw new InvalidOperationException("Slot has not been initialized.");
            if (index < 0 || index >= slot.Count)
                throw new InvalidOperationException($"Index out of range when storing to slot: {index}");
            slot[index] = Pop();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void ExecuteTry(int catchOffset, int finallyOffset)
        {
            if (catchOffset == 0 && finallyOffset == 0)
                throw new InvalidOperationException($"catchOffset and finallyOffset can't be 0 in a TRY block");
            int catchPointer = catchOffset == 0 ? -1 : checked(CurrentContext.InstructionPointer + catchOffset);
            int finallyPointer = finallyOffset == 0 ? -1 : checked(CurrentContext.InstructionPointer + finallyOffset);
            CurrentContext.TryStack ??= new Stack<ExceptionHandlingContext>();
            CurrentContext.TryStack.Push(new ExceptionHandlingContext(catchPointer, finallyPointer));
        }

        private void HandleException()
        {
            int pop = 0;
            foreach (var executionContext in InvocationStack)
            {
                if (executionContext.TryStack != null)
                {
                    while (executionContext.TryStack.TryPeek(out var tryContext))
                    {
                        if (tryContext.State == ExceptionHandlingState.Finally || (tryContext.State == ExceptionHandlingState.Catch && !tryContext.HasFinally))
                        {
                            executionContext.TryStack.Pop();
                            continue;
                        }
                        for (int i = 0; i < pop; i++)
                        {
                            ContextUnloaded(InvocationStack.Pop());
                        }
                        if (tryContext.State == ExceptionHandlingState.Try && tryContext.HasCatch)
                        {
                            tryContext.State = ExceptionHandlingState.Catch;
                            Push(UncaughtException);
                            executionContext.InstructionPointer = tryContext.CatchPointer;
                            UncaughtException = null;
                        }
                        else
                        {
                            tryContext.State = ExceptionHandlingState.Finally;
                            executionContext.InstructionPointer = tryContext.FinallyPointer;
                        }
                        return;
                    }
                }
                ++pop;
            }

            throw new Exception("An unhandled exception was thrown.");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public ExecutionContext LoadClonedContext(int initialPosition)
        {
            if (initialPosition < 0 || initialPosition > CurrentContext.Script.Length)
                throw new ArgumentOutOfRangeException(nameof(initialPosition));
            ExecutionContext context = CurrentContext.Clone();
            context.InstructionPointer = initialPosition;
            LoadContext(context);
            return context;
        }

        protected virtual void LoadContext(ExecutionContext context)
        {
            if (InvocationStack.Count >= MaxInvocationStackSize)
                throw new InvalidOperationException();
            InvocationStack.Push(context);
            if (EntryContext is null) EntryContext = context;
            CurrentContext = context;
        }

        public ExecutionContext LoadScript(Script script, int initialPosition = 0)
        {
            ExecutionContext context = new ExecutionContext(script, ReferenceCounter)
            {
                InstructionPointer = initialPosition
            };
            LoadContext(context);
            return context;
        }

        protected virtual void OnFault(Exception e)
        {
            State = VMState.FAULT;
        }

        protected virtual void OnStateChanged()
        {
        }

        protected virtual void OnSysCall(uint method)
        {
            throw new InvalidOperationException($"Syscall not found: {method}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StackItem Peek(int index = 0)
        {
            return CurrentContext.EvaluationStack.Peek(index);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public StackItem Pop()
        {
            return CurrentContext.EvaluationStack.Pop();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Pop<T>() where T : StackItem
        {
            return CurrentContext.EvaluationStack.Pop<T>();
        }

        protected virtual void PostExecuteInstruction(Instruction instruction)
        {
            if (ReferenceCounter.CheckZeroReferred() > MaxStackSize)
                throw new InvalidOperationException($"MaxStackSize exceed: {ReferenceCounter.Count}");
        }

        protected virtual void PreExecuteInstruction() { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Push(StackItem item)
        {
            CurrentContext.EvaluationStack.Push(item);
        }

        public void Throw(StackItem ex)
        {
            UncaughtException = ex;
            HandleException();
        }
    }
}
