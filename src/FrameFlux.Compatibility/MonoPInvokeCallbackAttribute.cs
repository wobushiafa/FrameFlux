// Unity's IL2CPP recognizes MonoPInvokeCallbackAttribute by name. Keeping it local
// lets the precompiled core expose reverse P/Invoke entry points without UnityEngine.
namespace AOT
{
    [System.AttributeUsage(System.AttributeTargets.Method)]
    internal sealed class MonoPInvokeCallbackAttribute : System.Attribute
    {
        public MonoPInvokeCallbackAttribute(System.Type delegateType) { }
    }
}
