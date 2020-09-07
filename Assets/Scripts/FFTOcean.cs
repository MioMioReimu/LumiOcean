using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
public class FFTOcean : MonoBehaviour
{
    public int fftSize = 1024;

    public float phillpsFactor = 1.0f;
    public float xzFactor = 1.0f;
    public float heightScale = 1.0f;
    public float bubbleScale = 1.0f;
    public float bubbleThreshold = 1.0f;
    public ComputeShader fftShader;
    public ComputeShader oceanShader;

    // wind speed
    public Vector2 windSpeed = new Vector2(10.0f, 10.0f);
    public float timeScale = 1.0f;

    // grid mesh
    public Vector2Int gridSize = new Vector2Int(512, 512);
    public float gridScale = 1.0f;
    private int[] vtxIndices;
    private Vector3[] vertices;
    private Vector2[] uvs;
    private Mesh gridMesh;

    private FFT heightFFT;
    private FFT slopXFFT;
    private FFT slopZFFT;
    private FFT displacementXFFT;
    private FFT displacementZFFT;

    private const int THREAD_SIZE = 1;

    private const int KERNEL_GEN_GAUSSIAN = 0;
    private const int KERNEL_GEN_SPECTRUM = 1;
    private const int KERNEL_GEN_VERTEX_INFO = 2;
    private const int KERNEL_GEN_PHILLIPS = 3;

    private const string SHADER_NAME_N = "n";
    private const string SHADER_NAME_WIND = "wind";
    private const string SHADER_NAME_WIND_DIR = "wind_dir";
    private const string SHADER_NAME_PHILLIPS_FACTOR = "phillips_factor";
    private const string SHADER_NAME_TIME = "time";
    private const string SHADER_NAME_HEIGHT_FACTOR = "height_factor";
    private const string SHADER_NAME_DISPLACEMENT_FACTOR = "displacement_factor";

    private const string SHADER_TEX_RAND = "rand_tex";
    private const string SHADER_TEX_HEIGHT = "height_spectrum_tex";
    private const string SHADER_TEX_DISPLACE_X = "displace_spectrum_x_tex";
    private const string SHADER_TEX_DISPLACE_Z = "displace_spectrum_z_tex";
    private const string SHADER_TEX_SLOP_X = "slop_spectrum_x_tex";
    private const string SHADER_TEX_SLOP_Z = "slop_spectrum_z_tex";

    private const string SHADER_TEX_VERTEX = "pos_tex";
    private const string SHADER_TEX_NORMAL_FOAM = "normal_tex";

    private const string SHADER_TEX_INIT_SPECTRUM = "init_spectrum_tex";
    private const string SHADER_TEX_PHILLIPS = "phillips_tex";

    private bool isGaussianGen = false;

    public RenderTexture gaussianTexture;
    public RenderTexture phillipsTexture;
    public RenderTexture initSpectrumTexture;
    public RenderTexture heightTexture;
    public RenderTexture slopXTexture;
    public RenderTexture slopZTexture;
    public RenderTexture displaceXTexture;
    public RenderTexture displaceZTexture;

    public RenderTexture heightOutTexture;

    public RenderTexture vtxTexture;
    public RenderTexture normalTexture;

    public Material oceanMaterial;
    public void Awake()
    {
        var meshFilter = gameObject.GetComponent<MeshFilter>();
        if (meshFilter == null)
        {
            meshFilter = gameObject.AddComponent<MeshFilter>();
        }
        var meshRenderer = gameObject.GetComponent<MeshRenderer>();
        if (meshRenderer == null)
        {
            meshRenderer = gameObject.AddComponent<MeshRenderer>();
        }
        oceanMaterial = meshRenderer.sharedMaterial;
    }

    void OnEnable()
    {
        Clean();
        GenGridMesh();
        Init();
        CalcOceanInfo();
    }

    void Clean()
    {
        if (heightFFT != null)
        {
            heightFFT.Dispose();
            heightFFT = null;
        }
        if (slopXFFT != null)
        {
            slopXFFT.Dispose();
            slopXFFT = null;
        }
        if (slopZFFT != null)
        {
            slopZFFT.Dispose();
            slopZFFT = null;
        }
        if (displacementXFFT != null)
        {
            displacementXFFT.Dispose();
            displacementXFFT = null;
        }
        if (displacementZFFT != null)
        {
            displacementZFFT.Dispose();
            displacementZFFT = null;
        }

        if (heightTexture) heightTexture.Release();
        if (slopXTexture) slopXTexture.Release();
        if (slopZTexture) slopZTexture.Release();
        if (displaceXTexture) displaceXTexture.Release();
        if (displaceZTexture) displaceZTexture.Release();
        heightTexture = null;
        slopXTexture = null;
        slopZTexture = null;
        displaceXTexture = null;
        displaceZTexture = null;

        if (initSpectrumTexture) initSpectrumTexture.Release();
        if (phillipsTexture) phillipsTexture.Release();
        initSpectrumTexture = null;
        phillipsTexture = null;

        
    }

    private void Init()
    {
        MeshFilter meshFilter = GetComponent<MeshFilter>();
        if (meshFilter)
        {
            meshFilter.sharedMesh = gridMesh;
        }


        heightFFT = new FFT(fftShader);
        displacementXFFT = new FFT(fftShader);
        displacementZFFT = new FFT(fftShader);
        slopXFFT = new FFT(fftShader);
        slopZFFT = new FFT(fftShader);

        // set ocean shader constant uniform variables.
        oceanShader.SetInt(SHADER_NAME_N, fftSize);

        float[] windSpeedArray = { windSpeed.x, windSpeed.y };
        oceanShader.SetFloats(SHADER_NAME_WIND, windSpeedArray);

        Vector2 windDir = windSpeed.normalized;
        float[] windDirArray = { windDir.x, windDir.y };
        oceanShader.SetFloats(SHADER_NAME_WIND_DIR, windDirArray);

        oceanShader.SetFloat(SHADER_NAME_PHILLIPS_FACTOR, phillpsFactor);
        oceanShader.SetFloat(SHADER_NAME_HEIGHT_FACTOR, heightScale);
        oceanShader.SetFloat(SHADER_NAME_DISPLACEMENT_FACTOR, xzFactor);

        gaussianTexture = AllocateTexture();
        heightTexture = AllocateTexture();
        slopXTexture = AllocateTexture();
        slopZTexture = AllocateTexture();
        displaceXTexture = AllocateTexture();
        displaceZTexture = AllocateTexture();

        initSpectrumTexture = AllocateTexture();
        phillipsTexture = AllocateTexture();

        vtxTexture = AllocateTexture(RenderTextureFormat.ARGBFloat);
        normalTexture = AllocateTexture(RenderTextureFormat.ARGBFloat);

        heightFFT.Init(heightTexture);
        slopXFFT.Init(slopXTexture);
        slopZFFT.Init(slopZTexture);
        displacementXFFT.Init(displaceXTexture);
        displacementZFFT.Init(displaceZTexture);
    }

    private RenderTexture AllocateTexture(RenderTextureFormat format=RenderTextureFormat.RGFloat)
    {
        RenderTexture tex = new RenderTexture(fftSize, fftSize, 0, format, RenderTextureReadWrite.Linear);
        tex.filterMode = FilterMode.Point;
        tex.wrapMode = TextureWrapMode.Clamp;
        tex.enableRandomWrite = true;
        tex.Create();
        return tex;
    }

    [MenuItem("Assets/Save RenderTexture to file")]
    public static void SaveRTToFile()
    {
        RenderTexture rt = Selection.activeObject as RenderTexture;

        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGB24, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        RenderTexture.active = null;

        byte[] bytes;
        bytes = tex.EncodeToPNG();

        string path = AssetDatabase.GetAssetPath(rt) + ".png";
        System.IO.File.WriteAllBytes(path, bytes);
        AssetDatabase.ImportAsset(path);
        Debug.Log("Saved to " + path);
    }

    [MenuItem("Assets/Save RenderTexture to file", true)]
    public static bool SaveRTToFileValidation()
    {
        return Selection.activeObject is RenderTexture;
    }

    // calcuate ocean info using fft.
    private void CalcOceanInfo()
    {
        // pass 0: generate gaussian texture
        if (!isGaussianGen)
        {
            oceanShader.SetTexture(KERNEL_GEN_GAUSSIAN, SHADER_TEX_RAND, gaussianTexture);
            oceanShader.Dispatch(KERNEL_GEN_GAUSSIAN, fftSize / THREAD_SIZE, fftSize / THREAD_SIZE, 1);
        }

        oceanShader.SetTexture(KERNEL_GEN_PHILLIPS, SHADER_TEX_RAND, gaussianTexture);
        oceanShader.SetTexture(KERNEL_GEN_PHILLIPS, SHADER_TEX_INIT_SPECTRUM, initSpectrumTexture);
        oceanShader.SetTexture(KERNEL_GEN_PHILLIPS, SHADER_TEX_PHILLIPS, phillipsTexture);
        oceanShader.Dispatch(KERNEL_GEN_PHILLIPS, fftSize / THREAD_SIZE, fftSize / THREAD_SIZE, 1);

        // pass 1: gen texture
        oceanShader.SetTexture(KERNEL_GEN_SPECTRUM, SHADER_TEX_RAND, gaussianTexture);
        oceanShader.SetTexture(KERNEL_GEN_SPECTRUM, SHADER_TEX_HEIGHT, heightTexture);
        oceanShader.SetTexture(KERNEL_GEN_SPECTRUM, SHADER_TEX_SLOP_X, slopXTexture);
        oceanShader.SetTexture(KERNEL_GEN_SPECTRUM, SHADER_TEX_SLOP_Z, slopZTexture);
        oceanShader.SetTexture(KERNEL_GEN_SPECTRUM, SHADER_TEX_DISPLACE_X, displaceXTexture);
        oceanShader.SetTexture(KERNEL_GEN_SPECTRUM, SHADER_TEX_DISPLACE_Z, displaceZTexture);
        oceanShader.Dispatch(KERNEL_GEN_SPECTRUM, fftSize / THREAD_SIZE, fftSize / THREAD_SIZE, 1);

        // pass fft: perform fft for height, slopx, slopz, displacex, displacez
        RenderTexture heightTex = heightFFT.IDFT();
        heightOutTexture = heightTex;
        RenderTexture slopXTex = slopXFFT.IDFT();
        RenderTexture slopZTex = slopZFFT.IDFT();
        RenderTexture displaceXTex = displacementXFFT.IDFT();
        RenderTexture displaceZTex = displacementZFFT.IDFT();

        if (oceanMaterial)
        {
            oceanMaterial.SetTexture("_DisplaceTex", heightOutTexture);
        }

        oceanShader.SetTexture(KERNEL_GEN_VERTEX_INFO, SHADER_TEX_HEIGHT, heightTex);
        oceanShader.SetTexture(KERNEL_GEN_VERTEX_INFO, SHADER_TEX_SLOP_X, slopXTex);
        oceanShader.SetTexture(KERNEL_GEN_VERTEX_INFO, SHADER_TEX_SLOP_Z, slopZTex);
        oceanShader.SetTexture(KERNEL_GEN_VERTEX_INFO, SHADER_TEX_DISPLACE_X, displaceXTex);
        oceanShader.SetTexture(KERNEL_GEN_VERTEX_INFO, SHADER_TEX_DISPLACE_Z, displaceZTex);
        oceanShader.SetTexture(KERNEL_GEN_VERTEX_INFO, SHADER_TEX_VERTEX, vtxTexture);
        oceanShader.SetTexture(KERNEL_GEN_VERTEX_INFO, SHADER_TEX_NORMAL_FOAM, normalTexture);
        oceanShader.Dispatch(KERNEL_GEN_VERTEX_INFO, fftSize / THREAD_SIZE, fftSize / THREAD_SIZE, 1);
    }

    void GenGridMesh()
    {
       
        vertices = new Vector3[(gridSize.x + 1) * (gridSize.y + 1)];
        vtxIndices = new int[gridSize.x * gridSize.y * 6];
        uvs = new Vector2[(gridSize.x + 1) * (gridSize.y + 1)];
        int index = 0;
        for (int i = 0; i < gridSize.y + 1; ++i)
        {
            for (int j = 0; j < gridSize.x + 1; ++j)
            {
                int p = i * (gridSize.y + 1) + j;
                vertices[p] = new Vector3(
                    (j - (gridSize.x + 1) / 2) * gridScale, 0.0f,
                    (i - (gridSize.y + 1) / 2) * gridScale);
                uvs[p] = new Vector2(
                    j / (float)(gridSize.x + 1),
                    i / (float)(gridSize.y + 1)
                    );
                if (i != gridSize.y&& j != gridSize.x)
                {
                    vtxIndices[index++] = p;
                    vtxIndices[index++] = p + gridSize.y + 1;
                    vtxIndices[index++] = p + gridSize.y + 2;

                    vtxIndices[index++] = p;
                    vtxIndices[index++] = p + gridSize.y + 2;
                    vtxIndices[index++] = p + 1;
                }
            }
        }

        if (!gridMesh)
        {
            gridMesh = new Mesh();
            if (vtxIndices.Length > 65535)
            {
                gridMesh.indexFormat = IndexFormat.UInt32;
            }
        }

        gridMesh.vertices = vertices;
        gridMesh.uv = uvs;
        gridMesh.triangles = vtxIndices;

        
    }

    void OnDisable()
    {
        
    }

    // Start is called before the first frame update
    void Start()
    {
        
    }

    // Update is called once per frame
    void Update()
    {
        if (oceanShader)
        {
            oceanShader.SetFloat(SHADER_NAME_TIME, Time.time);
        }
        CalcOceanInfo();
    }
}
