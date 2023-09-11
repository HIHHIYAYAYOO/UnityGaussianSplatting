using System;
using System.Collections.Generic;
using System.IO;
using TinyJson;
using Unity.Burst;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEditor;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

[BurstCompile]
public class GaussianSplatAssetCreator : EditorWindow
{
    const string kPointCloudPly = "point_cloud/iteration_7000/point_cloud.ply";
    const string kPointCloud30kPly = "point_cloud/iteration_30000/point_cloud.ply";
    const string kCamerasJson = "cameras.json";

    readonly FolderPickerPropertyDrawer m_FolderPicker = new();

    [SerializeField] string m_InputFolder;
    [SerializeField] bool m_Use30k = true;

    [SerializeField] string m_OutputFolder = "Assets/GaussianAssets";

    string m_ErrorMessage;

    [MenuItem("Tools/Create Gaussian Splat Asset")]
    public static void Init()
    {
        var window = GetWindowWithRect<GaussianSplatAssetCreator>(new Rect(50, 50, 500, 500), false, "Gaussian Splat Creator", true);
        window.minSize = new Vector2(200, 200);
        window.maxSize = new Vector2(1500, 1500);
        window.Show();
    }

    void OnGUI()
    {
        EditorGUILayout.Space();
        GUILayout.Label("Input data", EditorStyles.boldLabel);
        var rect = EditorGUILayout.GetControlRect(true);
        m_InputFolder = m_FolderPicker.PathFieldGUI(rect, new GUIContent("Input Folder"), m_InputFolder, kPointCloudPly, "PointCloudFolder");
        m_Use30k = EditorGUILayout.Toggle(new GUIContent("Use 30k Version", "Use iteration_30000 point cloud if available. Otherwise uses iteration_7000."), m_Use30k);

        EditorGUILayout.Space();
        GUILayout.Label("Output", EditorStyles.boldLabel);
        rect = EditorGUILayout.GetControlRect(true);
        m_OutputFolder = m_FolderPicker.PathFieldGUI(rect, new GUIContent("Output Folder"), m_OutputFolder, null, "GaussianAssetOutputFolder");

        EditorGUILayout.Space();
        GUILayout.BeginHorizontal();
        GUILayout.Space(30);
        if (GUILayout.Button("Create Asset"))
        {
            CreateAsset();
        }
        GUILayout.Space(30);
        GUILayout.EndHorizontal();

        if (!string.IsNullOrWhiteSpace(m_ErrorMessage))
        {
            EditorGUILayout.HelpBox(m_ErrorMessage, MessageType.Error);
        }
    }

    // input file splat data is expected to be in this format
    public struct InputSplatData
    {
        public Vector3 pos;
        public Vector3 nor;
        public Vector3 dc0;
        public Vector3 sh1, sh2, sh3, sh4, sh5, sh6, sh7, sh8, sh9, shA, shB, shC, shD, shE, shF;
        public float opacity;
        public Vector3 scale;
        public Quaternion rot;
    }


    static T CreateOrReplaceAsset<T>(T asset, string path) where T : UnityEngine.Object
    {
        T result = AssetDatabase.LoadAssetAtPath<T>(path);
        if (result == null)
        {
            AssetDatabase.CreateAsset(asset, path);
            result = asset;
        }
        else
        {
            if (typeof(Mesh).IsAssignableFrom(typeof(T))) { (result as Mesh)?.Clear(); }
            EditorUtility.CopySerialized(asset, result);
        }
        return result;
    }

    void CreateAsset()
    {
        m_ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(m_InputFolder))
        {
            m_ErrorMessage = $"Select input folder";
            return;
        }

        if (string.IsNullOrWhiteSpace(m_OutputFolder) || !m_OutputFolder.StartsWith("Assets/"))
        {
            m_ErrorMessage = $"Output folder must be within project, was '{m_OutputFolder}'";
            return;
        }
        Directory.CreateDirectory(m_OutputFolder);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Reading cameras info", 0.0f);
        GaussianSplatAsset.CameraInfo[] cameras = LoadJsonCamerasFile(m_InputFolder);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Reading PLY file", 0.1f);
        using NativeArray<InputSplatData> inputSplats = LoadPLYSplatFile(m_InputFolder, m_Use30k);
        if (inputSplats.Length == 0)
        {
            EditorUtility.ClearProgressBar();
            return;
        }

        string baseName = Path.GetFileNameWithoutExtension(m_InputFolder) + (m_Use30k ? "_30k" : "_7k");

        var assetPath = $"{m_OutputFolder}/{baseName}.asset";

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Creating texture objects", 0.2f);
        AssetDatabase.StartAssetEditing();

        GaussianSplatAsset asset = ScriptableObject.CreateInstance<GaussianSplatAsset>();
        asset.name = baseName;
        asset.m_Cameras = cameras;

        List<string> imageFiles = CreateTextureFiles(inputSplats, asset, m_OutputFolder, baseName);

        // files are created, import them so we can get to the importer objects, ugh
        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Initial texture import", 0.3f);
        AssetDatabase.StopAssetEditing();
        AssetDatabase.Refresh(ImportAssetOptions.ForceUncompressedImport);
        AssetDatabase.StartAssetEditing();

        // set their import settings
        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Set texture import settings", 0.4f);
        foreach (var ifile in imageFiles)
        {
            var imp = AssetImporter.GetAtPath(ifile) as TextureImporter;
            imp.isReadable = false;
            imp.mipmapEnabled = false;
            imp.npotScale = TextureImporterNPOTScale.None;
            imp.textureCompression = TextureImporterCompression.Uncompressed;
            imp.maxTextureSize = 8192;
            // obsolete API
#pragma warning disable CS0618
            imp.SetPlatformTextureSettings("Standalone", 8192, TextureImporterFormat.RGBAFloat);
#pragma warning restore CS0618
            AssetDatabase.ImportAsset(ifile);
        }
        AssetDatabase.StopAssetEditing();

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Setup textures onto asset", 0.8f);
        for (int i = 0; i < imageFiles.Count; ++i)
            asset.m_Tex[i] = AssetDatabase.LoadAssetAtPath<Texture2D>(imageFiles[i]);

        var savedAsset = CreateOrReplaceAsset(asset, assetPath);

        EditorUtility.DisplayProgressBar("Creating Gaussian Splat Asset", "Saving assets", 0.9f);
        AssetDatabase.SaveAssets();
        EditorUtility.ClearProgressBar();

        Selection.activeObject = savedAsset;
    }

    unsafe NativeArray<InputSplatData> LoadPLYSplatFile(string folder, bool use30k)
    {
        NativeArray<InputSplatData> data = default;
        string plyPath = $"{folder}/{(use30k ? kPointCloud30kPly : kPointCloudPly)}";
        if (!File.Exists(plyPath))
        {
            plyPath = $"{folder}/{kPointCloudPly}";
            if (!File.Exists(plyPath))
            {
                m_ErrorMessage = $"Did not find {plyPath} file";
                return data;
            }
        }

        int splatCount = 0;
        PLYFileReader.ReadFile(plyPath, out splatCount, out int vertexStride, out var plyAttrNames, out var verticesRawData);
        if (UnsafeUtility.SizeOf<InputSplatData>() != vertexStride)
        {
            m_ErrorMessage = $"InputVertex size mismatch, we expect {UnsafeUtility.SizeOf<InputSplatData>()} but file has {vertexStride}";
            return data;
        }

        // reorder SHs
        NativeArray<float> floatData = verticesRawData.Reinterpret<float>(1);
        ReorderSHs(splatCount, (float*)floatData.GetUnsafePtr());

        return verticesRawData.Reinterpret<InputSplatData>(1);
    }

    [BurstCompile]
    static unsafe void ReorderSHs(int splatCount, float* data)
    {
        int splatStride = UnsafeUtility.SizeOf<InputSplatData>() / 4;
        int shStartOffset = 9, shCount = 15;
        float* tmp = stackalloc float[shCount * 3];
        int idx = shStartOffset;
        for (int i = 0; i < splatCount; ++i)
        {
            for (int j = 0; j < shCount; ++j)
            {
                tmp[j * 3 + 0] = data[idx + j];
                tmp[j * 3 + 1] = data[idx + j + shCount];
                tmp[j * 3 + 2] = data[idx + j + shCount * 2];
            }

            for (int j = 0; j < shCount * 3; ++j)
            {
                data[idx + j] = tmp[j];
            }

            idx += splatStride;
        }
    }

    [BurstCompile]
    public struct InitTextureDataJob : IJob
    {
        public int width, height;
        public NativeArray<float3> dataPos;
        public NativeArray<float4> dataRot;
        public NativeArray<float3> dataScl;
        public NativeArray<float4> dataCol;
        public NativeArray<float3> dataSh1;
        public NativeArray<float3> dataSh2;
        public NativeArray<float3> dataSh3;
        public NativeArray<float3> dataSh4;
        public NativeArray<float3> dataSh5;
        public NativeArray<float3> dataSh6;
        public NativeArray<float3> dataSh7;
        public NativeArray<float3> dataSh8;
        public NativeArray<float3> dataSh9;
        public NativeArray<float3> dataShA;
        public NativeArray<float3> dataShB;
        public NativeArray<float3> dataShC;
        public NativeArray<float3> dataShD;
        public NativeArray<float3> dataShE;
        public NativeArray<float3> dataShF;

        [ReadOnly] public NativeArray<InputSplatData> inputSplats;
        public NativeArray<float3> bounds;

        public InitTextureDataJob(NativeArray<InputSplatData> input, NativeArray<float3> bounds)
        {
            inputSplats = input;
            this.bounds = bounds;

            const int kTextureWidth = 2048; //@TODO: bump to 4k
            width = kTextureWidth;
            height = math.max(1, (input.Length + width - 1) / width);
            // height multiple of compressed block heights
            int blockHeight = 4;
            height = (height + blockHeight - 1) / blockHeight * blockHeight;

            dataPos = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataRot = new NativeArray<float4>(width * height, Allocator.Persistent);
            dataScl = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataCol = new NativeArray<float4>(width * height, Allocator.Persistent);
            dataSh1 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh2 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh3 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh4 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh5 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh6 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh7 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh8 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataSh9 = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShA = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShB = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShC = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShD = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShE = new NativeArray<float3>(width * height, Allocator.Persistent);
            dataShF = new NativeArray<float3>(width * height, Allocator.Persistent);
        }

        public void Dispose()
        {
            dataPos.Dispose();
            dataRot.Dispose();
            dataScl.Dispose();
            dataCol.Dispose();
            dataSh1.Dispose();
            dataSh2.Dispose();
            dataSh3.Dispose();
            dataSh4.Dispose();
            dataSh5.Dispose();
            dataSh6.Dispose();
            dataSh7.Dispose();
            dataSh8.Dispose();
            dataSh9.Dispose();
            dataShA.Dispose();
            dataShB.Dispose();
            dataShC.Dispose();
            dataShD.Dispose();
            dataShE.Dispose();
            dataShF.Dispose();
        }

        public void Execute()
        {
            bounds[0] = float.PositiveInfinity;
            bounds[1] = float.NegativeInfinity;
            for (int i = 0; i < inputSplats.Length; ++i)
            {
                var splat = inputSplats[i];

                // pos
                float3 pos = splat.pos;
                bounds[0] = math.min(bounds[0], pos);
                bounds[1] = math.max(bounds[1], pos);
                dataPos[i] = pos;

                // rot
                var q = splat.rot;
                dataRot[i] = GaussianUtils.NormalizeSwizzleRotation(new float4(q.x, q.y, q.z, q.w));

                // scale
                dataScl[i] = GaussianUtils.LinearScale(splat.scale);

                // color
                var c = GaussianUtils.SH0ToColor(splat.dc0);
                var a = GaussianUtils.Sigmoid(splat.opacity);
                dataCol[i] = new float4(c.x, c.y, c.z, a);

                // SHs
                dataSh1[i] = splat.sh1;
                dataSh2[i] = splat.sh2;
                dataSh3[i] = splat.sh3;
                dataSh4[i] = splat.sh4;
                dataSh5[i] = splat.sh5;
                dataSh6[i] = splat.sh6;
                dataSh7[i] = splat.sh7;
                dataSh8[i] = splat.sh8;
                dataSh9[i] = splat.sh9;
                dataShA[i] = splat.shA;
                dataShB[i] = splat.shB;
                dataShC[i] = splat.shC;
                dataShD[i] = splat.shD;
                dataShE[i] = splat.shE;
                dataShF[i] = splat.shF;
            }
        }
    }

    static string SaveExr(string path, int width, int height, NativeArray<float3> data)
    {
        var exrData = ImageConversion.EncodeNativeArrayToEXR(data, GraphicsFormat.R32G32B32_SFloat, (uint)width, (uint)height, flags: Texture2D.EXRFlags.OutputAsFloat);
        File.WriteAllBytes(path, exrData.ToArray());
        exrData.Dispose();
        return path;
    }
    static string SaveExr(string path, int width, int height, NativeArray<float4> data)
    {
        var exrData = ImageConversion.EncodeNativeArrayToEXR(data, GraphicsFormat.R32G32B32A32_SFloat, (uint)width, (uint)height, flags: Texture2D.EXRFlags.OutputAsFloat);
        File.WriteAllBytes(path, exrData.ToArray());
        exrData.Dispose();
        return path;
    }

    static List<string> CreateTextureFiles(NativeArray<InputSplatData> inputSplats, GaussianSplatAsset asset, string folder, string baseName)
    {
        NativeArray<float3> bounds = new NativeArray<float3>(2, Allocator.TempJob);
        InitTextureDataJob texData = new InitTextureDataJob(inputSplats, bounds);
        texData.Schedule().Complete();
        asset.m_SplatCount = inputSplats.Length;
        asset.m_BoundsMin = bounds[0];
        asset.m_BoundsMax = bounds[1];
        bounds.Dispose();

        List<string> imageFiles = new();
        imageFiles.Add(SaveExr($"{folder}/{baseName}_pos.exr", texData.width, texData.height, texData.dataPos));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_rot.exr", texData.width, texData.height, texData.dataRot));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_scl.exr", texData.width, texData.height, texData.dataScl));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_col.exr", texData.width, texData.height, texData.dataCol));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh1.exr", texData.width, texData.height, texData.dataSh1));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh2.exr", texData.width, texData.height, texData.dataSh2));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh3.exr", texData.width, texData.height, texData.dataSh3));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh4.exr", texData.width, texData.height, texData.dataSh4));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh5.exr", texData.width, texData.height, texData.dataSh5));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh6.exr", texData.width, texData.height, texData.dataSh6));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh7.exr", texData.width, texData.height, texData.dataSh7));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh8.exr", texData.width, texData.height, texData.dataSh8));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sh9.exr", texData.width, texData.height, texData.dataSh9));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_sha.exr", texData.width, texData.height, texData.dataShA));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_shb.exr", texData.width, texData.height, texData.dataShB));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_shc.exr", texData.width, texData.height, texData.dataShC));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_shd.exr", texData.width, texData.height, texData.dataShD));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_she.exr", texData.width, texData.height, texData.dataShE));
        imageFiles.Add(SaveExr($"{folder}/{baseName}_shf.exr", texData.width, texData.height, texData.dataShF));

        texData.Dispose();
        return imageFiles;
    }

    static GaussianSplatAsset.CameraInfo[] LoadJsonCamerasFile(string folder)
    {
        string path = $"{folder}/{kCamerasJson}";
        if (!File.Exists(path))
            return null;

        string json = File.ReadAllText(path);
        var jsonCameras = JSONParser.FromJson<List<JsonCamera>>(json);
        if (jsonCameras == null || jsonCameras.Count == 0)
            return null;

        var result = new GaussianSplatAsset.CameraInfo[jsonCameras.Count];
        for (var camIndex = 0; camIndex < jsonCameras.Count; camIndex++)
        {
            var jsonCam = jsonCameras[camIndex];
            var pos = new Vector3(jsonCam.position[0], jsonCam.position[1], jsonCam.position[2]);
            // the matrix is a "view matrix", not "camera matrix" lol
            var axisx = new Vector3(jsonCam.rotation[0][0], jsonCam.rotation[1][0], jsonCam.rotation[2][0]);
            var axisy = new Vector3(jsonCam.rotation[0][1], jsonCam.rotation[1][1], jsonCam.rotation[2][1]);
            var axisz = new Vector3(jsonCam.rotation[0][2], jsonCam.rotation[1][2], jsonCam.rotation[2][2]);

            pos.z *= -1;
            axisy *= -1;
            axisx.z *= -1;
            axisy.z *= -1;
            axisz.z *= -1;

            var cam = new GaussianSplatAsset.CameraInfo
            {
                pos = pos,
                axisX = axisx,
                axisY = axisy,
                axisZ = axisz,
                fov = 25 //@TODO
            };
            result[camIndex] = cam;
        }

        return result;
    }

    [Serializable]
    public class JsonCamera
    {
        public int id;
        public string img_name;
        public int width;
        public int height;
        public float[] position;
        public float[][] rotation;
        public float fx;
        public float fy;
    }

}
