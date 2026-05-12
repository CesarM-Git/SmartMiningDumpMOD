// IgnoresAccessChecksToAttribute is recognized by the Mono/CoreCLR JIT by its
// fully-qualified name. The BCL does not ship a public declaration of it — each
// assembly that wants to bypass cross-assembly visibility checks must declare
// the attribute in System.Runtime.CompilerServices itself.
//
// We need this because BuildDynamicInspectorType emits a subclass of
// Mafi.Unity.Ui.Inspectors.MineTowerInspector, which is `internal class`. Without
// IgnoresAccessChecksTo, the emitted ctor's `call base..ctor` throws
// MethodAccessException at JIT/invoke time (see Player.log 17:02:41).
//
// We attach the attribute to BOTH:
//   1. This static assembly (SmartMiningDumpMOD)   — see below.
//   2. The dynamic assembly (SmartMiningDumpMOD.Dynamic) at emit time,
//      via CustomAttributeBuilder in BuildDynamicInspectorType.
// (Only #2 is strictly required for the inspector subclass, since the offending
// IL lives there; #1 is cheap insurance for any future reflection-based code.)

namespace System.Runtime.CompilerServices
{
    [AttributeUsage(AttributeTargets.Assembly, AllowMultiple = true)]
    internal sealed class IgnoresAccessChecksToAttribute : Attribute
    {
        public IgnoresAccessChecksToAttribute(string assemblyName)
        {
            AssemblyName = assemblyName;
        }

        public string AssemblyName { get; }
    }
}

[assembly: System.Runtime.CompilerServices.IgnoresAccessChecksTo("Mafi.Unity")]
