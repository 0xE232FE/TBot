using System.Reflection;

namespace TBot.Tests;

public class StrictInterfaceProxy : DispatchProxy {
    private Func<MethodInfo, object[], object> _handler;
    protected override object Invoke(MethodInfo method, object[] args) => _handler(method, args);

    public static T Make<T>(Func<MethodInfo, object[], object> handler) where T : class {
        var proxy = Create<T, StrictInterfaceProxy>();
        ((StrictInterfaceProxy)(object)proxy)._handler = handler;
        return proxy;
    }
}
