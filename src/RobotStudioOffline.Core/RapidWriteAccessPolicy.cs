namespace RobotStudioOffline.Core;

public enum RapidAccessDecision { Reuse, Request, Refuse }

public static class RapidWriteAccessPolicy
{
    public static RapidAccessDecision Decide(bool heldByMe, bool held, bool externalEnabled, bool virtualLoad)
    {
        if (heldByMe) return RapidAccessDecision.Reuse;
        return virtualLoad && !held && externalEnabled ? RapidAccessDecision.Request : RapidAccessDecision.Refuse;
    }
}
