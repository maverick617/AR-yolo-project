using System;
using UnityEditor;
using UnityEngine;

namespace AR80sRetroEditor
{
    public static class PhoneModelSetup
    {
        private const string ModelPath =
            "Assets/AR80sRetro/Models/phone/Model/old_nokia_phone.obj";
        private const string TexturePath =
            "Assets/AR80sRetro/Models/phone/Texture/old_nokia_phone_baseColor.jpg";
        private const string MaterialDirectory =
            "Assets/AR80sRetro/Models/phone/Material";
        private const string PrefabPath =
            "Assets/AR80sRetro/Models/phone/prefab/phone.prefab";
        private const float NominalPhoneHeightMeters = 0.15f;

        [MenuItem("Tools/AR 80s Retro/Rebuild Old Nokia Phone Prefab")]
        public static void ConfigurePhoneModel()
        {
            AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
            ConfigureTextureImporter();
            ConfigureModelImporter();

            GameObject model =
                AssetDatabase.LoadAssetAtPath<GameObject>(ModelPath);
            Texture2D texture =
                AssetDatabase.LoadAssetAtPath<Texture2D>(TexturePath);
            if (model == null || texture == null)
            {
                throw new InvalidOperationException(
                    $"Phone source assets could not be imported: model={model}, "
                    + $"texture={texture}.");
            }

            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                throw new InvalidOperationException(
                    "Universal Render Pipeline/Lit shader was not found.");
            }

            Material back = CreateOrUpdateMaterial(
                $"{MaterialDirectory}/Phone_Back.mat",
                shader,
                texture,
                metallic: 0f,
                smoothness: 0.15f,
                emission: false);
            Material side = CreateOrUpdateMaterial(
                $"{MaterialDirectory}/Phone_Side.mat",
                shader,
                texture,
                metallic: 0f,
                smoothness: 0.59f,
                emission: false);
            Material screen = CreateOrUpdateMaterial(
                $"{MaterialDirectory}/Phone_Screen.mat",
                shader,
                texture,
                metallic: 0.47f,
                smoothness: 0.95f,
                emission: true);

            GameObject root = new GameObject("phone");
            try
            {
                GameObject visual =
                    PrefabUtility.InstantiatePrefab(model) as GameObject;
                if (visual == null)
                {
                    visual = UnityEngine.Object.Instantiate(model);
                }

                visual.name = "Old Nokia Phone Low Poly Visual";
                PrefabUtility.UnpackPrefabInstance(
                    visual,
                    PrefabUnpackMode.Completely,
                    InteractionMode.AutomatedAction);
                visual.transform.SetParent(root.transform, false);
                visual.transform.localPosition = Vector3.zero;
                visual.transform.localRotation = Quaternion.identity;
                visual.transform.localScale = Vector3.one;

                Renderer[] renderers =
                    visual.GetComponentsInChildren<Renderer>(true);
                if (renderers.Length == 0)
                {
                    throw new InvalidOperationException(
                        "The imported Nokia model contains no renderers.");
                }

                for (int i = 0; i < renderers.Length; i++)
                {
                    Renderer renderer = renderers[i];
                    Material selected = SelectMaterial(
                        renderer.gameObject.name,
                        back,
                        side,
                        screen);
                    Material[] assigned =
                        new Material[Mathf.Max(1, renderer.sharedMaterials.Length)];
                    for (int materialIndex = 0;
                        materialIndex < assigned.Length;
                        materialIndex++)
                    {
                        assigned[materialIndex] = selected;
                    }

                    renderer.sharedMaterials = assigned;
                }

                if (!TryGetLocalRendererBounds(root.transform, out Bounds bounds)
                    || bounds.size.y <= 0.0001f)
                {
                    throw new InvalidOperationException(
                        "Could not calculate Nokia model bounds.");
                }

                float uniformScale =
                    NominalPhoneHeightMeters / bounds.size.y;
                visual.transform.localScale =
                    Vector3.one * uniformScale;
                if (!TryGetLocalRendererBounds(root.transform, out bounds))
                {
                    throw new InvalidOperationException(
                        "Could not calculate scaled Nokia model bounds.");
                }

                visual.transform.localPosition -= bounds.center;
                bool success;
                PrefabUtility.SaveAsPrefabAsset(root, PrefabPath, out success);
                if (!success)
                {
                    throw new InvalidOperationException(
                        $"Failed to save phone Prefab at '{PrefabPath}'.");
                }

                AssetDatabase.SaveAssets();
                Debug.Log(
                    $"Old Nokia phone Prefab rebuilt at '{PrefabPath}'. "
                    + $"Nominal bounds={bounds.size}, renderers={renderers.Length}.");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        private static void ConfigureTextureImporter()
        {
            TextureImporter importer =
                AssetImporter.GetAtPath(TexturePath) as TextureImporter;
            if (importer == null)
            {
                throw new InvalidOperationException(
                    $"Texture importer was not found for '{TexturePath}'.");
            }

            importer.sRGBTexture = true;
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

            importer.globalScale = 0.001f;
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
            string path,
            Shader shader,
            Texture texture,
            float metallic,
            float smoothness,
            bool emission)
        {
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }
            else
            {
                material.shader = shader;
            }

            material.name = System.IO.Path.GetFileNameWithoutExtension(path);
            material.SetTexture("_BaseMap", texture);
            material.SetTexture("_MainTex", texture);
            material.SetColor("_BaseColor", Color.white);
            material.SetColor("_Color", Color.white);
            material.SetFloat("_Metallic", metallic);
            material.SetFloat("_Smoothness", smoothness);
            if (emission)
            {
                material.EnableKeyword("_EMISSION");
                material.globalIlluminationFlags =
                    MaterialGlobalIlluminationFlags.RealtimeEmissive;
                material.SetTexture("_EmissionMap", texture);
                material.SetColor(
                    "_EmissionColor",
                    new Color(0.35f, 0.45f, 0.35f, 1f));
            }
            else
            {
                material.DisableKeyword("_EMISSION");
                material.globalIlluminationFlags =
                    MaterialGlobalIlluminationFlags.EmissiveIsBlack;
                material.SetTexture("_EmissionMap", null);
                material.SetColor("_EmissionColor", Color.black);
            }

            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material SelectMaterial(
            string objectName,
            Material back,
            Material side,
            Material screen)
        {
            string normalized = objectName?.ToLowerInvariant() ?? string.Empty;
            if (normalized.Contains("screen"))
            {
                return screen;
            }

            if (normalized.Contains("side"))
            {
                return side;
            }

            return back;
        }

        private static bool TryGetLocalRendererBounds(
            Transform reference,
            out Bounds result)
        {
            result = default;
            bool hasBounds = false;
            Renderer[] renderers =
                reference.GetComponentsInChildren<Renderer>(true);
            for (int rendererIndex = 0;
                rendererIndex < renderers.Length;
                rendererIndex++)
            {
                Bounds bounds = renderers[rendererIndex].bounds;
                Vector3 min = bounds.min;
                Vector3 max = bounds.max;
                for (int corner = 0; corner < 8; corner++)
                {
                    Vector3 worldCorner = new Vector3(
                        (corner & 1) == 0 ? min.x : max.x,
                        (corner & 2) == 0 ? min.y : max.y,
                        (corner & 4) == 0 ? min.z : max.z);
                    Vector3 localCorner =
                        reference.InverseTransformPoint(worldCorner);
                    if (!hasBounds)
                    {
                        result = new Bounds(localCorner, Vector3.zero);
                        hasBounds = true;
                    }
                    else
                    {
                        result.Encapsulate(localCorner);
                    }
                }
            }

            return hasBounds;
        }
    }
}
