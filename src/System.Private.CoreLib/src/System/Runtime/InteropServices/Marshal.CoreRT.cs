// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Diagnostics;
using System.Reflection;
using System.Text;

using Internal.Runtime.Augments;


namespace System
{
    // For System.Private.Reflection.Core's sake
    [CLSCompliant(false)]
    public class __ComObject { }
}

namespace System.Runtime.InteropServices
{
    partial class Marshal
    {
        internal static int SizeOfHelper(Type t, bool throwIfNotMarshalable)
        {
            Debug.Assert(throwIfNotMarshalable);
            return RuntimeAugments.InteropCallbacks.GetStructUnsafeStructSize(t.TypeHandle);
        }

        public static IntPtr OffsetOf(Type t, string fieldName)
        {
            if (t == null)
                throw new ArgumentNullException(nameof(t));

            if (String.IsNullOrEmpty(fieldName))
                throw new ArgumentNullException(nameof(fieldName));

            if (t.TypeHandle.IsGenericType() || t.TypeHandle.IsGenericTypeDefinition())
                throw new ArgumentException(SR.Argument_NeedNonGenericType, nameof(t));

            return new IntPtr(RuntimeAugments.InteropCallbacks.GetStructFieldOffset(t.TypeHandle, fieldName));
        }

        private static object PtrToStructureHelper(IntPtr ptr, Type structureType)
        {
            Object boxedStruct = InteropExtensions.RuntimeNewObject(structureType.TypeHandle);
            PtrToStructureImpl(ptr, boxedStruct);
            return boxedStruct;
        }

        private static void PtrToStructureHelper(IntPtr ptr, object structure, bool allowValueClasses)
        {
            if (ptr == IntPtr.Zero)
                throw new ArgumentNullException(nameof(ptr));

            if (structure == null)
                throw new ArgumentNullException(nameof(structure));

            if (!allowValueClasses && structure.EETypePtr.IsValueType)
            {
                throw new ArgumentException(nameof(structure), SR.Argument_StructMustNotBeValueClass);
            }

            PtrToStructureImpl(ptr, structure);
        }

        private static unsafe void PtrToStructureImpl(IntPtr ptr, object structure)
        {
            RuntimeTypeHandle structureTypeHandle = structure.GetType().TypeHandle;

            // Boxed struct start at offset 1 (EEType* at offset 0) while class start at offset 0
            int offset = structureTypeHandle.IsValueType() ? 1 : 0;

            IntPtr unmarshalStub;
            if (structureTypeHandle.IsBlittable())
            {
                if (!RuntimeAugments.InteropCallbacks.TryGetStructUnmarshalStub(structureTypeHandle, out unmarshalStub))
                {
                    unmarshalStub = IntPtr.Zero;
                }
            }
            else
            {
                unmarshalStub = RuntimeAugments.InteropCallbacks.GetStructUnmarshalStub(structureTypeHandle);
            }

            if (unmarshalStub != IntPtr.Zero)
            {
                InteropExtensions.PinObjectAndCall(structure,
                    unboxedStructPtr =>
                    {
                        CalliIntrinsics.Call<int>(
                            unmarshalStub,
                            (void*)ptr,                                     // unsafe (no need to adjust as it is always struct)
                            ((void*)((IntPtr*)unboxedStructPtr + offset))   // safe (need to adjust offset as it could be class)
                        );
                    });
            }
            else
            {
                int structSize = Marshal.SizeOf(structure);
                InteropExtensions.PinObjectAndCall(structure,
                    unboxedStructPtr =>
                    {
                        InteropExtensions.Memcpy(
                            (IntPtr)((IntPtr*)unboxedStructPtr + offset),   // safe (need to adjust offset as it could be class)
                            ptr,                                            // unsafe (no need to adjust as it is always struct)
                            structSize
                        );
                    });
            }
        }

        public static unsafe void DestroyStructure(IntPtr ptr, Type structuretype)
        {
            if (ptr == IntPtr.Zero)
                throw new ArgumentNullException(nameof(ptr));

            if (structuretype == null)
                throw new ArgumentNullException(nameof(structuretype));

            RuntimeTypeHandle structureTypeHandle = structuretype.TypeHandle;

            if (structureTypeHandle.IsGenericType() || structureTypeHandle.IsGenericTypeDefinition())
                throw new ArgumentException(SR.Argument_NeedNonGenericType, "t");

            if (structureTypeHandle.IsEnum() ||
                structureTypeHandle.IsInterface() ||
                InteropExtensions.AreTypesAssignable(typeof(Delegate).TypeHandle, structureTypeHandle))
            {
                throw new ArgumentException(SR.Format(SR.Argument_MustHaveLayoutOrBeBlittable, structureTypeHandle.LastResortToString));
            }

            if (structureTypeHandle.IsBlittable())
            {
                // ok to call with blittable structure, but no work to do in this case.
                return;
            }

            IntPtr destroyStructureStub = RuntimeAugments.InteropCallbacks.GetDestroyStructureStub(structureTypeHandle, out bool hasInvalidLayout);
            if (hasInvalidLayout)
                throw new ArgumentException(SR.Format(SR.Argument_MustHaveLayoutOrBeBlittable, structureTypeHandle.LastResortToString));
            // DestroyStructureStub == IntPtr.Zero means its fields don't need to be destroied
            if (destroyStructureStub != IntPtr.Zero)
            {
                CalliIntrinsics.Call<int>(
                    destroyStructureStub,
                    (void*)ptr                                     // unsafe (no need to adjust as it is always struct)
                );
            }
        }

        public static unsafe void StructureToPtr(object structure, IntPtr ptr, bool fDeleteOld)
        {
            if (structure == null)
                throw new ArgumentNullException(nameof(structure));

            if (ptr == IntPtr.Zero)
                throw new ArgumentNullException(nameof(ptr));

            if (fDeleteOld)
            {
                DestroyStructure(ptr, structure.GetType());
            }

            RuntimeTypeHandle structureTypeHandle = structure.GetType().TypeHandle;

            if (structureTypeHandle.IsGenericType() || structureTypeHandle.IsGenericTypeDefinition())
            {
                throw new ArgumentException(nameof(structure), SR.Argument_NeedNonGenericObject);
            }

            // Boxed struct start at offset 1 (EEType* at offset 0) while class start at offset 0
            int offset = structureTypeHandle.IsValueType() ? 1 : 0;

            IntPtr marshalStub;
            if (structureTypeHandle.IsBlittable())
            {
                if (!RuntimeAugments.InteropCallbacks.TryGetStructMarshalStub(structureTypeHandle, out marshalStub))
                {
                    marshalStub = IntPtr.Zero;
                }
            }
            else
            {
                marshalStub = RuntimeAugments.InteropCallbacks.GetStructMarshalStub(structureTypeHandle);
            }

            if (marshalStub != IntPtr.Zero)
            {
                InteropExtensions.PinObjectAndCall(structure,
                    unboxedStructPtr =>
                    {
                        CalliIntrinsics.Call<int>(
                            marshalStub,
                            ((void*)((IntPtr*)unboxedStructPtr + offset)),  // safe (need to adjust offset as it could be class)
                            (void*)ptr                                      // unsafe (no need to adjust as it is always struct)
                        );
                    });
            }
            else
            {
                int structSize = Marshal.SizeOf(structure);
                InteropExtensions.PinObjectAndCall(structure,
                    unboxedStructPtr =>
                    {
                        InteropExtensions.Memcpy(
                            ptr,                                            // unsafe (no need to adjust as it is always struct)
                            (IntPtr)((IntPtr*)unboxedStructPtr + offset),   // safe (need to adjust offset as it could be class)
                            structSize
                        );
                    });
            }
        }

        internal static Exception GetExceptionForHRInternal(int errorCode, IntPtr errorInfo)
        {
            return new COMException()
            {
                HResult = errorCode
            };
        }

        public static void WriteInt16(object ptr, int ofs, short val)
        {
            // Obsolete
            throw new PlatformNotSupportedException("WriteInt16");
        }

        public static void WriteInt64(object ptr, int ofs, long val)
        {
            // Obsolete
            throw new PlatformNotSupportedException("WriteInt64");
        }

        private static void PrelinkCore(MethodInfo m)
        {
            // Note: This method is effectively a no-op in ahead-of-time compilation scenarios. In CoreCLR and Desktop, this will pre-generate
            // the P/Invoke, but everything is pre-generated in CoreRT.
        }

        internal static Delegate GetDelegateForFunctionPointerInternal(IntPtr ptr, Type t)
        {
            return PInvokeMarshal.GetDelegateForFunctionPointer(ptr, t.TypeHandle);
        }

        internal static IntPtr GetFunctionPointerForDelegateInternal(Delegate d)
        {
            return PInvokeMarshal.GetFunctionPointerForDelegate(d);
        }

        public static int GetLastWin32Error()
        {
            return PInvokeMarshal.GetLastWin32Error();
        }

        internal static void SetLastWin32Error(int errorCode)
        {
            PInvokeMarshal.SetLastWin32Error(errorCode);
        }

        internal static bool IsPinnable(object o)
        {
            return (o == null) || o.EETypePtr.MightBeBlittable();
        }

        public static IntPtr AllocHGlobal(IntPtr cb)
        {
            return PInvokeMarshal.AllocHGlobal(cb);
        }

        public static void FreeHGlobal(IntPtr hglobal)
        {
            PInvokeMarshal.FreeHGlobal(hglobal);
        }

        internal static IntPtr AllocBSTR(int length)
        {
            return PInvokeMarshal.AllocBSTR(length);
        }

        public static void FreeBSTR(IntPtr ptr)
        {
            PInvokeMarshal.FreeBSTR(ptr);
        }

        public static unsafe IntPtr AllocCoTaskMem(int cb)
        {
            return PInvokeMarshal.AllocCoTaskMem(cb);
        }

        public static void FreeCoTaskMem(IntPtr ptr)
        {
            PInvokeMarshal.FreeCoTaskMem(ptr);
        }

        public static int GetExceptionCode()
        {
            // Obsolete
            throw new PlatformNotSupportedException();
        }

        public static IntPtr GetExceptionPointers()
        {
            throw new PlatformNotSupportedException();
        }

        public static string PtrToStringBSTR(IntPtr ptr)
        {
#if PLATFORM_WINDOWS
            return PtrToStringUni(ptr, (int)Interop.OleAut32.SysStringLen(ptr));
#else
            throw new PlatformNotSupportedException();
#endif
        }

        public static byte ReadByte(object ptr, int ofs)
        {
            return ReadValueSlow(ptr, ofs, bytesNeeded: 1, ReadByte);
        }

        public static short ReadInt16(object ptr, int ofs)
        {
            return ReadValueSlow(ptr, ofs, bytesNeeded: 2, ReadInt16);
        }

        public static int ReadInt32(object ptr, int ofs)
        {
            return ReadValueSlow(ptr, ofs, bytesNeeded: 4, ReadInt32);
        }

        public static long ReadInt64(object ptr, int ofs)
        {
            return ReadValueSlow(ptr, ofs, bytesNeeded: 8, ReadInt64);
        }

        //====================================================================
        // Read value from marshaled object (marshaled using AsAny)
        // It's quite slow and can return back dangling pointers
        // It's only there for backcompact
        // People should instead use the IntPtr overloads
        //====================================================================
        private static unsafe T ReadValueSlow<T>(object ptr, int ofs, int bytesNeeded, Func<IntPtr, int, T> readValueHelper)
        {
            // Consumers of this method are documented to throw AccessViolationException on any AV
            if (ptr is null)
            {
                throw new AccessViolationException();
            }

            if (ptr.EETypePtr.IsArray ||
                ptr is string ||
                ptr is StringBuilder)
            {
                // We could implement these if really needed.
                throw new PlatformNotSupportedException();
            }

            // We are going to assume this is a Sequential or Explicit layout type because
            // we don't want to touch reflection metadata for this.
            // If we're wrong, this will throw the exception we get for missing interop data
            // instead of an ArgumentException.
            // That's quite acceptable for an obsoleted API.

            Type structType = ptr.GetType();
            
            int size = SizeOf(structType);

            // Compat note: CLR wouldn't bother with a range check. If someone does this,
            // they're likely taking dependency on some CLR implementation detail quirk.
            if (checked(ofs + bytesNeeded) > size)
                throw new ArgumentOutOfRangeException(nameof(ofs));

            IntPtr nativeBytes = AllocCoTaskMem(size);

            try
            {
                StructureToPtr(ptr, nativeBytes, false);
                return readValueHelper(nativeBytes, ofs);
            }
            finally
            {
                DestroyStructure(nativeBytes, structType);
                FreeCoTaskMem(nativeBytes);
            }
        }

        public static IntPtr ReAllocCoTaskMem(IntPtr pv, int cb)
        {
            return PInvokeMarshal.CoTaskMemReAlloc(pv, (IntPtr)cb);
        }

        public static IntPtr ReAllocHGlobal(IntPtr pv, IntPtr cb)
        {
            return PInvokeMarshal.MemReAlloc(pv, cb);
        }

        public static unsafe IntPtr StringToBSTR(string s)
        {
            if (s == null)
                return IntPtr.Zero;

            // Overflow checking
            if (s.Length + 1 < s.Length)
                throw new ArgumentOutOfRangeException(nameof(s));

#if PLATFORM_WINDOWS
            IntPtr bstr = Interop.OleAut32.SysAllocStringLen(s, s.Length);
            if (bstr == IntPtr.Zero)
                throw new OutOfMemoryException();

            return bstr;
#else
            throw new PlatformNotSupportedException();
#endif
        }

        public static void WriteByte(object ptr, int ofs, byte val)
        {
            // Obsolete
            throw new PlatformNotSupportedException("WriteByte");
        }

        public static void WriteInt32(object ptr, int ofs, int val)
        {
            // Obsolete
            throw new PlatformNotSupportedException("WriteInt32");
        }

        [McgIntrinsics]
        internal static unsafe partial class CalliIntrinsics
        {
            internal static T Call<T>(
            System.IntPtr pfn,
            void* arg0,
            void* arg1)
            {
                throw new NotSupportedException();
            }
            internal static T Call<T>(
            System.IntPtr pfn,
            void* arg0)
            {
                throw new NotSupportedException();
            }
        }
    }
}
