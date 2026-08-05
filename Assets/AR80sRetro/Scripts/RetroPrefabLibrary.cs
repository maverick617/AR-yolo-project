using System;
using System.Collections.Generic;
using UnityEngine;

namespace AR80sRetro
{
    [CreateAssetMenu(menuName = "AR 80s Retro/Prefab Library")]
    public sealed class RetroPrefabLibrary : ScriptableObject
    {
        [SerializeField] private List<RetroReplacementRule> rules = new List<RetroReplacementRule>();

        public IReadOnlyList<RetroReplacementRule> Rules => rules;

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
                    return true;
                }
            }

            return false;
        }

        public bool TryValidateUniqueAprilTagIds(out string error)
        {
            error = null;
            Dictionary<int, string> ownerByTagId = new Dictionary<int, string>();
            List<int> tagIds = new List<int>(8);

            for (int ruleIndex = 0; ruleIndex < rules.Count; ruleIndex++)
            {
                RetroReplacementRule rule = rules[ruleIndex];
                if (rule == null || !rule.UseAprilTagPose)
                {
                    continue;
                }

                if (!rule.HasExplicitAprilTagIdentity)
                {
                    error =
                        $"Rule '{rule.DetectionLabel}' accepts any AprilTag. "
                        + "Every simultaneously tracked object must have an explicit, unique Tag ID.";
                    return false;
                }

                tagIds.Clear();
                rule.GetConfiguredAprilTagIds(tagIds);
                if (tagIds.Count == 0)
                {
                    error =
                        $"Rule '{rule.DetectionLabel}' has no numeric AprilTag ID. "
                        + "Multi-object replacement requires explicit numeric IDs.";
                    return false;
                }

                HashSet<int> idsWithinRule = new HashSet<int>();
                for (int tagIndex = 0; tagIndex < tagIds.Count; tagIndex++)
                {
                    int tagId = tagIds[tagIndex];
                    if (!idsWithinRule.Add(tagId))
                    {
                        error =
                            $"Rule '{rule.DetectionLabel}' contains duplicate AprilTag ID {tagId}.";
                        return false;
                    }

                    if (ownerByTagId.TryGetValue(tagId, out string owner)
                        && !string.Equals(
                            owner,
                            rule.DetectionLabel,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        error =
                            $"AprilTag ID {tagId} is assigned to both '{owner}' "
                            + $"and '{rule.DetectionLabel}'. Tag IDs cannot be shared across objects.";
                        return false;
                    }

                    ownerByTagId[tagId] = rule.DetectionLabel;
                }
            }

            return true;
        }
    }
}
