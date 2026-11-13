using System.Runtime.Versioning;
using System.Security.AccessControl;

namespace Aura.Core.SystemIntegration;

[SupportedOSPlatform("windows")]
internal static class AppSignals
{
    // shortcut: fixed names can be occupied before startup; use a private namespace if hostile-user DoS matters.
    private const string Access = "D:P(A;;GA;;;SY)(A;;GA;;;BA)";

    public static Mutex CreateMutex(string name, out bool created)
    {
        var security = new MutexSecurity();
        security.SetSecurityDescriptorSddlForm(Access);
        return MutexAcl.Create(true, name, out created, security);
    }

    public static EventWaitHandle CreateEvent(string name)
    {
        var security = new EventWaitHandleSecurity();
        security.SetSecurityDescriptorSddlForm(Access);
        return EventWaitHandleAcl.Create(false, EventResetMode.AutoReset, name, out _, security);
    }
}
