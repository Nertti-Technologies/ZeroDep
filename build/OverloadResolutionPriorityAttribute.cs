// Lets the compiler prefer one overload over another (C# 13) on netstandard2.0; newer targets ship it.
// Compiled only into the ZeroDep facade for netstandard2.0 (see src/ZeroDep/ZeroDep.csproj).
#if NETSTANDARD2_0
namespace System.Runtime.CompilerServices
{
    using System.ComponentModel;

    /// <summary>Reserved compiler infrastructure to rank overloads on legacy targets.</summary>
    [EditorBrowsable(EditorBrowsableState.Never)]
    [AttributeUsage(AttributeTargets.Method | AttributeTargets.Constructor | AttributeTargets.Property, Inherited = false)]
    internal sealed class OverloadResolutionPriorityAttribute : Attribute
    {
        public OverloadResolutionPriorityAttribute(int priority) => Priority = priority;

        public int Priority { get; }
    }
}
#endif
