using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    [CreateAssetMenu(menuName = "AR 80s Retro/Prefab Library")]
    public sealed class RetroPrefabLibrary : ScriptableObject
    {
        [SerializeField] private List<RetroReplacementRule> rules = new List<RetroReplacementRule>();

        public bool TryGetRule(string detectionLabel, out RetroReplacementRule rule)
        {
            rule = null;

            if (string.IsNullOrWhiteSpace(detectionLabel))
            {
                return false;
            }

            for (int i = 0; i < rules.Count; i++)
            {
                RetroReplacementRule candidate = rules[i];
                if (candidate == null)
                {
                    continue;
                }

                if (string.Equals(candidate.DetectionLabel, detectionLabel, System.StringComparison.OrdinalIgnoreCase))
                {
                    rule = candidate;
                    return candidate.Prefab != null;
                }
            }

            return false;
        }
    }
}
