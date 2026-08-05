using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace AR80sRetroEditor
{
    public static class PenModelSetup
    {
        private const string ModelPath =
            "Assets/AR80sRetro/Models/pen/Model/old_pen.obj";
        private const string BaseColorPath =
            "Assets/AR80sRetro/Models/pen/Texture/old_pen_baseColor.png";
        private const string NormalPath =
            "Assets/AR80sRetro/Models/pen/Texture/old_pen_normal.png";
        private const string MaterialPath =
            "Assets/AR80sRetro/Models/pen/Material/Pen.mat";
        private const string PrefabPath =
            "Assets/AR80sRetro/Models/pen/prefab/pen.prefab";
        private const float NominalPenLengthMeters = 0.14f;

        [MenuItem("Tools/AR 80s Retro/Rebuild Old Pen Prefab")]
        public static void ConfigurePenModel()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            ConfigureTextureImporter(BaseColorPath, false);
            ConfigureTextureImporter(NormalPath, true);
            ConfigureModelImporter();

            GameObject model =
                AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            Texture2D baseColor =
                AssetDatabase.LoadAssetAtPath<Texture2D>(BaseColorPath);
            Texture2D normal =
                AssetDatabase.LoadAssetAtPath<Texture2D>(NormalPath);
            if (model == null || baseColor == null || normal == null)
            {
                throw new InvalidOperationException(
                    $"Pen source assets could not be imported: model={model}, "
                    + $"baseColor={baseColor}, normal={normal}.");
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Universal Render Pipeline/Lit shader was not found.");
            }

            Material material = CreateOrUpdateMaterial(
                shader,
                baseColor,
                normal);
            GameObject root = new GameObject("pen");
            try
            {
                GameObject visual =
                    PrefabUtility.InstantiatePrefab(model) as GameObject;
                if (visual == null)
                {
                    visual = UnityEngine.Object.Instantiate(model);
                }

                visual.name = "Old Pen Visual";
                PrefabUtility.UnpackPrefabInstance(
                    visual,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;

                if (!TryGetPrincipalTipDirection(
                    visual.transform,
                    out Vector3 sourceTipDirection))
                {
                    throw new InvalidOperationException(
                        "Could not calculate the pen shaft/tip axis.");
                }

                visual.transform.localRotation = Quaternion.FromToRotation(
                    sourceTipDirection,
                    Vector3.up);

                Renderer[] renderers =
                    visual.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    throw new InvalidOperationException(
                        "The imported pen model contains no renderers.");
                }

                for (int i = 0; i < renderers.Length; i++)
                {
                    Material[] assigned =
                        new Material[Mathf.Max(
                            1,
                            renderers[i].sharedMaterials.Length)];
                    for (int materialIndex = 0;
                        materialIndex < assigned.Length;
                        materialIndex++)
                    {
                        assigned[materialIndex] = material;
                    }

                    renderers[i].sharedMaterials = assigned;
                }

                if (!TryGetLocalRendererBounds(root.transform, out Bounds bounds)
                    || bounds.size.y <= 0.0001f)
                {
                    throw new InvalidOperationException(
                        "Could not calculate oriented pen bounds.");
                }

                float uniformScale =
                    NominalPenLengthMeters / bounds.size.y;
                visual.transform.localScale =
                    Vector3.one * uniformScale;
                if (!TryGetLocalRendererBounds(root.transform, out bounds))
                {
                    throw new InvalidOperationException(
                        "Could not calculate scaled pen bounds.");
                }

                visual.transform.localPosition -= bounds.center;
                bool success;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out success);
                if (!success)
                {
                    throw new InvalidOperationException(
                        $"Failed to save pen Prefab at '{PrefabPath}'.");
                }

                AssetDatabase.SaveAssets();
                Debug.Log(
                    $"Old pen Prefab rebuilt at '{PrefabPath}'. "
                    + $"Nominal bounds={bounds.size}, renderers={renderers.Length}, "
                    + "tipAxis=+Y.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void ConfigureTextureImporter(
            string assetPath,
            bool normalMap)
        {
            TextureImporter importer =
                AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException(
                    $"Texture importer was not found for '{assetPath}'.");
            }

            importer.textureType = normalMap
                ? TextureImporterType.NormalMap
                : TextureImporterType.Default;
            importer.sRGBTexture = !normalMap;
            importer.mipmapEnabled = true;
            importer.maxTextureSize = 1024;
            importer.textureCompression =
                TextureImporterCompression.Compressed;
            importer.SaveAndReimport();
        }

        private static void ConfigureModelImporter()
        {
            ModelImporter importer =
                AssetImporter.GetAtPath(ModelPath) as ModelImporter;
            if (importer == null)
            {
                throw new InvalidOperationException(
                    $"Model importer was not found for '{ModelPath}'.");
            }

            importer.globalScale = 1f;
            importer.isReadable = true;
            importer.importCameras = false;
            importer.importLights = false;
            importer.importVisibility = false;
            importer.importBlendShapes = false;
            importer.importAnimation = false;
            importer.materialImportMode =
                ModelImporterMaterialImportMode.None;
            importer.meshCompression = ModelImporterMeshCompression.Low;
            importer.optimizeMeshPolygons = true;
            importer.optimizeMeshVertices = true;
            importer.SaveAndReimport();
        }

        private static Material CreateOrUpdateMaterial(
            Shader shader,
            Texture baseColor,
            Texture normal)
        {
            Material material =
                AssetDatabase.LoadAssetAtPath<Material>(MaterialPath);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, MaterialPath);
            }
            else
            {
                material.shader = shader;
            }

            material.name = "Pen";
            material.SetTexture("_BaseMap", baseColor);
            material.SetTexture("_MainTex", baseColor);
            material.SetColor("_BaseColor", Color.white);
            material.SetColor("_Color", Color.white);
            material.SetTexture("_BumpMap", normal);
            material.SetFloat("_BumpScale", 1f);
            material.EnableKeyword("_NORMALMAP");
            material.SetFloat("_Metallic", 0.55f);
            material.SetFloat("_Smoothness", 0.4f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static bool TryGetLocalRendererBounds(
            Transform reference,
            out Bounds result)
        {
            result = default;
            bool hasBounds = false;
            MeshFilter[] filters =
                reference.GetComponentsInChildren<MeshFilter>(true);
            for (int i = 0; i < filters.Length; i++)
            {
                if (filters[i].sharedMesh == null)
                {
                    continue;
                }

                Vector3[] vertices = filters[i].sharedMesh.vertices;
                for (int vertexIndex = 0;
                    vertexIndex < vertices.Length;
                    vertexIndex++)
                {
                    Vector3 localPoint = reference.InverseTransformPoint(
                        filters[i].transform.TransformPoint(
                            vertices[vertexIndex]));
                    EncapsulatePoint(
                        localPoint,
                        ref result,
                        ref hasBounds);
                }
            }

            SkinnedMeshRenderer[] skinned =
                reference.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            for (int i = 0; i < skinned.Length; i++)
            {
                if (skinned[i].sharedMesh == null)
                {
                    continue;
                }

                EncapsulateBounds(
                    reference,
                    skinned[i].transform,
                    skinned[i].localBounds,
                    ref result,
                    ref hasBounds);
            }

            return hasBounds;
        }

        private static void EncapsulateBounds(
            Transform reference,
            Transform source,
            Bounds sourceBounds,
            ref Bounds result,
            ref bool hasBounds)
        {
            Vector3 min = sourceBounds.min;
            Vector3 max = sourceBounds.max;
            for (int corner = 0; corner < 8; corner++)
            {
                Vector3 sourceCorner = new Vector3(
                    (corner & 1) == 0 ? min.x : max.x,
                    (corner & 2) == 0 ? min.y : max.y,
                    (corner & 4) == 0 ? min.z : max.z);
                Vector3 localCorner = reference.InverseTransformPoint(
                    source.TransformPoint(sourceCorner));
                EncapsulatePoint(
                    localCorner,
                    ref result,
                    ref hasBounds);
            }
        }

        private static void EncapsulatePoint(
            Vector3 point,
            ref Bounds result,
            ref bool hasBounds)
        {
            if (!hasBounds)
            {
                result = new Bounds(point, Vector3.zero);
                hasBounds = true;
            }
            else
            {
                result.Encapsulate(point);
            }
        }

        private static bool TryGetPrincipalTipDirection(
            Transform visual,
            out Vector3 tipDirection)
        {
            List<Vector3> points = new List<Vector3>(2048);
            MeshFilter[] filters =
                visual.GetComponentsInChildren<MeshFilter>(true);
            for (int filterIndex = 0;
                filterIndex < filters.Length;
                filterIndex++)
            {
                Mesh mesh = filters[filterIndex].sharedMesh;
                if (mesh == null)
                {
                    continue;
                }

                Vector3[] vertices = mesh.vertices;
                for (int vertexIndex = 0;
                    vertexIndex < vertices.Length;
                    vertexIndex++)
                {
                    Vector3 worldPoint = filters[filterIndex].transform
                        .TransformPoint(vertices[vertexIndex]);
                    points.Add(visual.InverseTransformPoint(worldPoint));
                }
            }

            if (points.Count < 3)
            {
                tipDirection = Vector3.up;
                return false;
            }

            Vector3 center = Vector3.zero;
            for (int i = 0; i < points.Count; i++)
            {
                center += points[i];
            }
            center /= points.Count;

            float xx = 0f;
            float xy = 0f;
            float xz = 0f;
            float yy = 0f;
            float yz = 0f;
            float zz = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 offset = points[i] - center;
                xx += offset.x * offset.x;
                xy += offset.x * offset.y;
                xz += offset.x * offset.z;
                yy += offset.y * offset.y;
                yz += offset.y * offset.z;
                zz += offset.z * offset.z;
            }

            Vector3 axis = Vector3.one.normalized;
            for (int iteration = 0; iteration < 32; iteration++)
            {
                Vector3 next = new Vector3(
                    xx * axis.x + xy * axis.y + xz * axis.z,
                    xy * axis.x + yy * axis.y + yz * axis.z,
                    xz * axis.x + yz * axis.y + zz * axis.z);
                if (next.sqrMagnitude <= 0.000001f)
                {
                    tipDirection = Vector3.up;
                    return false;
                }

                axis = next.normalized;
            }

            float minimum = float.PositiveInfinity;
            float maximum = float.NegativeInfinity;
            for (int i = 0; i < points.Count; i++)
            {
                float projection = Vector3.Dot(points[i] - center, axis);
                minimum = Mathf.Min(minimum, projection);
                maximum = Mathf.Max(maximum, projection);
            }

            float endBand = Mathf.Max(0.0001f, (maximum - minimum) * 0.18f);
            float lowRadiusSum = 0f;
            float highRadiusSum = 0f;
            int lowCount = 0;
            int highCount = 0;
            for (int i = 0; i < points.Count; i++)
            {
                Vector3 offset = points[i] - center;
                float projection = Vector3.Dot(offset, axis);
                float radius =
                    (offset - axis * projection).magnitude;
                if (projection <= minimum + endBand)
                {
                    lowRadiusSum += radius;
                    lowCount++;
                }
                if (projection >= maximum - endBand)
                {
                    highRadiusSum += radius;
                    highCount++;
                }
            }

            if (lowCount == 0 || highCount == 0)
            {
                tipDirection = Vector3.up;
                return false;
            }

            float lowRadius = lowRadiusSum / lowCount;
            float highRadius = highRadiusSum / highCount;
            tipDirection = highRadius < lowRadius ? axis : -axis;
            return true;
        }
    }
}
