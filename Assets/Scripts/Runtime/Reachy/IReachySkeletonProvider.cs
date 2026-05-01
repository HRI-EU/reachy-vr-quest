using UnityEngine;

namespace ReachyMiniTeleop.Reachy
{
    public interface IReachySkeletonProvider
    {
        bool SkeletonReady { get; }
        bool TryGetTransform(string key, out Transform boneTransform);
        bool IsLeftHandTracked();
        bool IsRightHandTracked();
    }
}

