using System;
using UnityEngine;

namespace AR80sRetro
{
    /// <summary>
    /// Last-resort visuals for legacy prefab variants whose source FBX is absent.
    /// Shapes are normalized and then fitted to the measured mask/depth size.
    /// </summary>
    public static class ProceduralRetroFactory
    {
        public static GameObject Create(string label, Transform parent)
        {
            GameObject root = new GameObject($"Procedural Retro {label}");
            root.transform.SetParent(parent, false);
            Material material = CreateMaterial(label);

            if (EqualsLabel(label, "bottle"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Bottle Body",
                    new Vector3(0f, -0.08f, 0f), new Vector3(0.44f, 0.36f, 0.44f), material);
                AddPrimitive(root.transform, PrimitiveType.Sphere, "Bottle Shoulder",
                    new Vector3(0f, 0.31f, 0f), new Vector3(0.42f, 0.24f, 0.42f), material);
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Bottle Neck",
                    new Vector3(0f, 0.47f, 0f), new Vector3(0.18f, 0.12f, 0.18f), material);
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Bottle Cap",
                    new Vector3(0f, 0.62f, 0f), new Vector3(0.21f, 0.045f, 0.21f), material);
            }
            else if (EqualsLabel(label, "cup"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Cup Body",
                    new Vector3(0f, -0.04f, 0f), new Vector3(0.58f, 0.46f, 0.58f), material);
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Cup Rim",
                    new Vector3(0f, 0.45f, 0f), new Vector3(0.34f, 0.035f, 0.34f), material);
                AddPrimitive(root.transform, PrimitiveType.Cube, "Cup Handle Outer",
                    new Vector3(0.38f, 0.1f, 0f), new Vector3(0.22f, 0.34f, 0.08f), material);
            }
            else if (EqualsLabel(label, "phone"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cube, "Phone",
                    Vector3.zero, new Vector3(0.52f, 1f, 0.07f), material);
            }
            else if (EqualsLabel(label, "tv"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cube, "TV Cabinet",
                    new Vector3(0f, 0.12f, 0f), new Vector3(1f, 0.68f, 0.12f), material);
                AddPrimitive(root.transform, PrimitiveType.Cube, "TV Screen",
                    new Vector3(0f, 0.12f, -0.065f), new Vector3(0.86f, 0.54f, 0.02f),
                    CreateScreenMaterial());
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "TV Stand",
                    new Vector3(0f, -0.34f, 0.02f), new Vector3(0.13f, 0.12f, 0.13f), material);
                AddPrimitive(root.transform, PrimitiveType.Cube, "TV Base",
                    new Vector3(0f, -0.48f, 0.02f), new Vector3(0.5f, 0.06f, 0.3f), material);
            }
            else if (EqualsLabel(label, "chair"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cube, "Seat",
                    new Vector3(0f, 0.05f, 0f), new Vector3(0.75f, 0.12f, 0.75f), material);
                AddPrimitive(root.transform, PrimitiveType.Cube, "Back",
                    new Vector3(0f, 0.47f, 0.31f), new Vector3(0.75f, 0.75f, 0.12f), material);
                AddFourLegs(root.transform, -0.38f, material);
            }
            else if (EqualsLabel(label, "couch"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cube, "Couch Seat",
                    new Vector3(0f, -0.15f, 0f), new Vector3(1f, 0.28f, 0.52f), material);
                AddPrimitive(root.transform, PrimitiveType.Cube, "Couch Back",
                    new Vector3(0f, 0.25f, 0.22f), new Vector3(1f, 0.65f, 0.14f), material);
                AddPrimitive(root.transform, PrimitiveType.Cube, "Left Arm",
                    new Vector3(-0.46f, 0.02f, 0f), new Vector3(0.12f, 0.42f, 0.55f), material);
                AddPrimitive(root.transform, PrimitiveType.Cube, "Right Arm",
                    new Vector3(0.46f, 0.02f, 0f), new Vector3(0.12f, 0.42f, 0.55f), material);
            }
            else if (EqualsLabel(label, "plant"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Plant Pot",
                    new Vector3(0f, -0.33f, 0f), new Vector3(0.48f, 0.24f, 0.48f), material);
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Plant Stem",
                    new Vector3(0f, 0.08f, 0f), new Vector3(0.1f, 0.35f, 0.1f), material);
                AddPrimitive(root.transform, PrimitiveType.Sphere, "Plant Crown",
                    new Vector3(0f, 0.45f, 0f), new Vector3(0.72f, 0.62f, 0.72f), material);
            }
            else if (EqualsLabel(label, "table"))
            {
                AddPrimitive(root.transform, PrimitiveType.Cube, "Table Top",
                    new Vector3(0f, 0.38f, 0f), new Vector3(1f, 0.12f, 0.72f), material);
                AddFourLegs(root.transform, -0.08f, material);
            }
            else
            {
                AddPrimitive(root.transform, PrimitiveType.Cylinder, "Fallback Object",
                    Vector3.zero, new Vector3(0.6f, 0.5f, 0.6f), material);
            }

            return root;
        }

        private static void AddFourLegs(Transform root, float centerY, Material material)
        {
            float[] coordinates = { -0.3f, 0.3f };
            for (int x = 0; x < coordinates.Length; x++)
            {
                for (int z = 0; z < coordinates.Length; z++)
                {
                    AddPrimitive(root, PrimitiveType.Cube, "Leg",
                        new Vector3(coordinates[x], centerY, coordinates[z]),
                        new Vector3(0.1f, 0.7f, 0.1f), material);
                }
            }
        }

        private static void AddPrimitive(
            Transform parent,
            PrimitiveType type,
            string name,
            Vector3 localPosition,
            Vector3 localScale,
            Material material,
            Quaternion? localRotation = null)
        {
            GameObject primitive = GameObject.CreatePrimitive(type);
            primitive.name = name;
            primitive.transform.SetParent(parent, false);
            primitive.transform.localPosition = localPosition;
            primitive.transform.localRotation = localRotation ?? Quaternion.identity;
            primitive.transform.localScale = localScale;
            Collider collider = primitive.GetComponent<Collider>();
            if (collider != null)
            {
                UnityEngine.Object.Destroy(collider);
            }

            Renderer renderer = primitive.GetComponent<Renderer>();
            if (renderer != null && material != null)
            {
                renderer.sharedMaterial = material;
            }
        }

        private static Material CreateMaterial(string label)
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                Debug.LogWarning(
                    "No runtime fallback shader is available. The primitive will use Unity's default material.");
                return null;
            }

            Material material = new Material(shader)
            {
                name = $"Procedural Retro {label} Material"
            };
            Color color = EqualsLabel(label, "bottle")
                ? new Color(0.1f, 0.75f, 0.92f, 1f)
                : EqualsLabel(label, "plant")
                    ? new Color(0.15f, 0.72f, 0.3f, 1f)
                    : new Color(0.9f, 0.25f, 0.62f, 1f);
            material.color = color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Metallic"))
            {
                material.SetFloat("_Metallic", 0.25f);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.35f);
            }

            return material;
        }

        private static Material CreateScreenMaterial()
        {
            Shader shader = Shader.Find("Universal Render Pipeline/Lit");
            if (shader == null)
            {
                shader = Shader.Find("Standard");
            }

            if (shader == null)
            {
                return null;
            }

            Material material = new Material(shader)
            {
                name = "Procedural Retro TV Screen Material"
            };
            Color color = new Color(0.04f, 0.07f, 0.13f, 1f);
            material.color = color;
            if (material.HasProperty("_BaseColor"))
            {
                material.SetColor("_BaseColor", color);
            }

            if (material.HasProperty("_Smoothness"))
            {
                material.SetFloat("_Smoothness", 0.8f);
            }

            return material;
        }

        private static bool EqualsLabel(string first, string second)
        {
            return string.Equals(first, second, StringComparison.OrdinalIgnoreCase);
        }
    }
}
