namespace RobotStudioOffline.Core;

public static class BridgeOperations
{
    public static readonly string[] All = ["station", "robot_pose", "reach", "jump", "collisions", "simulation", "screenshot",
        .. DevelopmentRequests.Operations, .. IoRequests.Operations, .. GeometryRequests.Operations, .. SceneRequests.Operations,
        .. RunRequests.Operations, .. RapidModuleRequests.Operations, .. EngineeringRequests.Operations,
        .. BodyRequests.Operations, .. MotionRequests.Operations, .. SmartRequests.Operations, "station_logic"];
}
