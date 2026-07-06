using System.Collections.Generic;
using UnityEngine;

// =====================================================================================
// TelaCompleta.cs
// Gera a tela inteira ("assopre para" + PRO / PA / GAR) como uma teia de triangulação
// Delaunay, tudo por código, sem precisar desenhar contornos na mão.
//
// COMO FUNCIONA:
// 1) Gera o texto (título + palavra) direto via TextGenerator (motor interno do uGUI),
//    sem Canvas nenhum, desenha as meshes numa câmera offscreen (RenderTexture) e lê
//    os pixels pra montar uma máscara booleana (true = dentro de alguma letra).
// 2) Espalha pontos dentro da máscara (rejection sampling) + pontos extras ao redor
//    pra criar aquele efeito de "teia" saindo das letras (igual a referência).
// 3) Roda Bowyer-Watson (Delaunay) nesses pontos.
// 4) Desenha tudo com GL.LINES no OnPostRender: a malha fina de triângulos + um
//    contorno mais grosso ao longo da borda da máscara (silhueta das letras).
//
// SETUP NA UNITY:
// - Cria uma Camera (Orthographic) e arrasta esse script nela.
// - Arrasta uma Font (de preferência uma bold/condensada, tipo a da referência) no
//   campo "fonteTitulo" e "fontePalavra".
// - "resolucaoTela" é a resolução LÓGICA em que o conteúdo é desenhado, sempre em
//   portrait (ex: 1080x1920), independente de como a TV está fisicamente ligada.
// - Se a TV é um painel landscape (ex: 1920x1080) deitado fisicamente pra virar um
//   totem portrait: deixa "rotacionar90" marcado, e configura a resolução do Player
//   (Project Settings > Player > Resolution) na orientação NATIVA da TV (landscape).
//   O script gira a câmera pra compensar, então o resultado final sai em pé na tela.
//   Se sair de cabeça pra baixo, marca "inverterSentido".
// - O material das linhas usa um shader Unlit/Color interno, não precisa criar nada.
// =====================================================================================

[RequireComponent(typeof(Camera))]
public class tela : MonoBehaviour
{
    [Header("Textos")]
    public string tituloTexto = "assopre para";
    public string[] linhasPalavra = { "PRO", "PA", "GAR" };

    [Header("Fontes")]
    public Font fonteTitulo;
    public Font fontePalavra;

    [Header("Layout (em pixels da textura de máscara)")]
    public Vector2Int resolucaoTela = new Vector2Int(1080, 1920);
    [Range(0.3f, 0.95f)] public float larguraPalavraRelativa = 0.85f; // largura ocupada pela palavra
    [Range(0.02f, 0.3f)] public float alturaTituloRelativa = 0.06f;
    [Range(0.5f, 0.95f)] public float alturaPalavraRelativa = 0.78f; // altura total ocupada pelas 3 linhas

    [Header("Amostragem de pontos")]
    public float espacamentoPontos = 10f;      // distância mínima entre pontos dentro das letras
    public int pontosExtrasFundo = 900;        // pontos soltos fora das letras (teia de fundo)
    public float raioContorno = 3f;            // resolução da borda (silhueta) das letras

    [Header("Rotação física (TV landscape usada em pé como totem)")]
    public bool rotacionar90 = true;
    public bool inverterSentido = false; // se ficar de cabeça pra baixo, marca isso

    [Header("Visual")]
    public Color corFundo = new Color(0.94f, 0.90f, 0.78f);
    public Color corLinhas = new Color(0.25f, 0.18f, 0.1f, 0.55f);
    public Color corContorno = new Color(0.15f, 0.1f, 0.05f, 1f);
    public float espessuraLinha = 1f;
    public float espessuraContorno = 4f;

    // internos
    private bool[,] mascara;
    private List<Vector2> pontos = new List<Vector2>();
    private List<int> trianguloIndices = new List<int>(); // trios de índices em "pontos"
    private List<Vector2> pontosContorno = new List<Vector2>();
    private Material matLinha;
    private float escalaMundoPorPixel;
    private Vector2 origemMundo;

    void Start()
    {
        CriarMaterial();
        ConfigurarCameraFisica();
        GerarMascara();
        GerarPontos();
        Triangular();
        ExtrairContorno();
        CalcularTransformacaoMundo();
    }

    // -------------------------------------------------------------------------------
    // ROTAÇÃO FÍSICA: a TV é um painel landscape (ex: 1920x1080) montado deitado pra
    // funcionar como totem portrait. Em vez de girar os pontos, giramos a própria
    // câmera 90°. O conteúdo continua sendo desenhado normalmente em coordenadas
    // portrait (resolucaoTela.x = largura menor, resolucaoTela.y = altura maior).
    //
    // IMPORTANTE: a resolução do Player (Project Settings > Player, ou a janela do
    // Game view) precisa estar na orientação NATIVA da TV (landscape, ex: 1920x1080),
    // não na resolução portrait. É a rotação da câmera que resolve o resto.
    // -------------------------------------------------------------------------------
    void ConfigurarCameraFisica()
    {
        Camera cam = GetComponent<Camera>();
        if (rotacionar90)
        {
            float angulo = inverterSentido ? -90f : 90f;
            transform.rotation = Quaternion.Euler(0f, 0f, angulo);
            // depois de girar, a "altura" que a câmera enxerga precisa cobrir a
            // LARGURA do conteúdo portrait (resolucaoTela.x)
            cam.orthographicSize = resolucaoTela.x * 0.5f;
        }
        else
        {
            transform.rotation = Quaternion.identity;
            cam.orthographicSize = resolucaoTela.y * 0.5f;
        }
    }

    void CriarMaterial()
    {
        Shader shader = Shader.Find("Hidden/Internal-Colored");
        matLinha = new Material(shader);
        matLinha.hideFlags = HideFlags.HideAndDontSave;
        matLinha.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        matLinha.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        matLinha.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
        matLinha.SetInt("_ZWrite", 0);
    }

    // -------------------------------------------------------------------------------
    // 1) MÁSCARA: gera o texto via TextGenerator numa câmera offscreen e lê os pixels
    // -------------------------------------------------------------------------------
    void GerarMascara()
    {
        int w = resolucaoTela.x;
        int h = resolucaoTela.y;

        GameObject camGO = new GameObject("MaskCam_TEMP");
        Camera maskCam = camGO.AddComponent<Camera>();
        maskCam.clearFlags = CameraClearFlags.SolidColor;
        maskCam.backgroundColor = Color.black;
        maskCam.orthographic = true;
        maskCam.orthographicSize = h / 2f;
        maskCam.nearClipPlane = 0.1f;
        maskCam.farClipPlane = 10f;
        maskCam.transform.position = new Vector3(0, 0, -5);

        RenderTexture rt = new RenderTexture(w, h, 16, RenderTextureFormat.ARGB32);
        maskCam.targetTexture = rt;

        // Gera as meshes de texto direto com TextGenerator (o motor interno do uGUI),
        // sem passar por Canvas/RectTransform — assim não depende de nenhum ciclo de
        // layout pra calcular o tamanho, é tudo determinístico e imediato.
        List<(Mesh mesh, Material mat)> pecas = new List<(Mesh, Material)>();

        float alturaTituloPx = h * alturaTituloRelativa;
        pecas.Add(GerarPecaTexto(tituloTexto, fonteTitulo,
            new Vector2(0, h * 0.5f - alturaTituloPx * 0.7f), w * 0.9f, alturaTituloPx));

        float topoPalavra = h * 0.5f - alturaTituloPx * 1.6f;
        float alturaTotalPalavra = h * alturaPalavraRelativa;
        float alturaLinha = alturaTotalPalavra / linhasPalavra.Length;
        for (int i = 0; i < linhasPalavra.Length; i++)
        {
            float y = topoPalavra - alturaLinha * (i + 0.5f);
            pecas.Add(GerarPecaTexto(linhasPalavra[i], fontePalavra,
                new Vector2(0, y), w * larguraPalavraRelativa, alturaLinha * 0.92f));
        }

        DesenhaMeshTemp desenhador = camGO.AddComponent<DesenhaMeshTemp>();
        desenhador.itens = pecas;

        maskCam.Render();

        RenderTexture ativo = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
        tex.Apply();
        RenderTexture.active = ativo;

        mascara = new bool[w, h];
        Color32[] px = tex.GetPixels32();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                mascara[x, y] = px[y * w + x].r > 40; // texto branco sobre fundo preto

        // limpeza
        Destroy(tex);
        rt.Release();
        foreach (var p in pecas) if (p.mesh != null) Destroy(p.mesh);
        Destroy(camGO);
    }

    // Monta a mesh de um bloco de texto (com best-fit dentro de largura x altura),
    // já deslocada pra posCentro. Devolve a mesh e o material (textura da fonte).
    (Mesh mesh, Material mat) GerarPecaTexto(string texto, Font fonte, Vector2 posCentro, float largura, float altura)
    {
        if (fonte == null) fonte = Resources.GetBuiltinResource<Font>("Arial.ttf");

        TextGenerator gerador = new TextGenerator();
        TextGenerationSettings config = new TextGenerationSettings
        {
            font = fonte,
            fontSize = 10,
            fontStyle = FontStyle.Bold,
            color = Color.white,
            lineSpacing = 1f,
            richText = false,
            textAnchor = TextAnchor.MiddleCenter,
            resizeTextForBestFit = true,
            resizeTextMinSize = 10,
            resizeTextMaxSize = 3000,
            horizontalOverflow = HorizontalWrapMode.Overflow,
            verticalOverflow = VerticalWrapMode.Overflow,
            generationExtents = new Vector2(largura, altura),
            pivot = new Vector2(0.5f, 0.5f),
            generateOutOfBounds = true,
            scaleFactor = 1f
        };

        gerador.Populate(texto, config);
        IList<UIVertex> vertsOriginais = gerador.verts;
        int total = vertsOriginais.Count;

        List<Vector3> vertices = new List<Vector3>(total);
        List<Vector2> uvs = new List<Vector2>(total);
        List<Color32> cores = new List<Color32>(total);
        List<int> indices = new List<int>(total * 6 / 4);

        for (int i = 0; i < total; i++)
        {
            UIVertex v = vertsOriginais[i];
            vertices.Add(new Vector3(v.position.x + posCentro.x, v.position.y + posCentro.y, 0f));
            uvs.Add(v.uv0);
            cores.Add(v.color);
        }
        for (int i = 0; i + 3 < total; i += 4)
        {
            indices.Add(i); indices.Add(i + 1); indices.Add(i + 2);
            indices.Add(i + 2); indices.Add(i + 3); indices.Add(i);
        }

        Mesh mesh = new Mesh();
        mesh.SetVertices(vertices);
        mesh.SetUVs(0, uvs);
        mesh.SetColors(cores);
        mesh.SetTriangles(indices, 0);
        mesh.RecalculateBounds();

        return (mesh, fonte.material);
    }

    // Componente auxiliar: desenha as meshes de texto durante o Render() manual da
    // câmera de máscara. Precisa ser um MonoBehaviour porque Graphics.DrawMeshNow só
    // funciona dentro do callback de renderização de uma câmera.
    private class DesenhaMeshTemp : MonoBehaviour
    {
        public List<(Mesh mesh, Material mat)> itens;
        void OnPostRender()
        {
            if (itens == null) return;
            foreach (var it in itens)
            {
                if (it.mesh == null || it.mat == null) continue;
                it.mat.SetPass(0);
                Graphics.DrawMeshNow(it.mesh, Matrix4x4.identity);
            }
        }
    }

    // -------------------------------------------------------------------------------
    // 2) PONTOS: dentro da máscara (denso) + fundo (esparso, fora das letras)
    // -------------------------------------------------------------------------------
    void GerarPontos()
    {
        pontos.Clear();
        int w = resolucaoTela.x, h = resolucaoTela.y;

        // grade regular jitterizada dentro das letras
        for (float y = 0; y < h; y += espacamentoPontos)
        {
            for (float x = 0; x < w; x += espacamentoPontos)
            {
                int xi = Mathf.Clamp((int)x, 0, w - 1);
                int yi = Mathf.Clamp((int)y, 0, h - 1);
                if (mascara[xi, yi])
                {
                    float jx = x + Random.Range(-espacamentoPontos * 0.4f, espacamentoPontos * 0.4f);
                    float jy = y + Random.Range(-espacamentoPontos * 0.4f, espacamentoPontos * 0.4f);
                    pontos.Add(new Vector2(jx, jy));
                }
            }
        }

        // pontos soltos no fundo pra dar aquele efeito de teia ao redor das letras
        for (int i = 0; i < pontosExtrasFundo; i++)
        {
            pontos.Add(new Vector2(Random.Range(0, w), Random.Range(0, h)));
        }

        // cantos, pra malha cobrir a tela inteira até a borda
        pontos.Add(new Vector2(0, 0));
        pontos.Add(new Vector2(w, 0));
        pontos.Add(new Vector2(0, h));
        pontos.Add(new Vector2(w, h));
    }

    // -------------------------------------------------------------------------------
    // 3) DELAUNAY (Bowyer-Watson)
    // -------------------------------------------------------------------------------
    struct Tri { public int a, b, c; public Vector2 centro; public float raio2; }

    void Triangular()
    {
        trianguloIndices.Clear();
        int n = pontos.Count;
        if (n < 3) return;

        float minX = 0, minY = 0, maxX = resolucaoTela.x, maxY = resolucaoTela.y;
        float dx = maxX - minX, dy = maxY - minY;
        float deltaMax = Mathf.Max(dx, dy) * 10f;
        Vector2 midp = new Vector2((minX + maxX) / 2f, (minY + maxY) / 2f);

        List<Vector2> pts = new List<Vector2>(pontos);
        int i0 = pts.Count, i1 = pts.Count + 1, i2 = pts.Count + 2;
        pts.Add(new Vector2(midp.x - deltaMax, midp.y - deltaMax));
        pts.Add(new Vector2(midp.x, midp.y + deltaMax));
        pts.Add(new Vector2(midp.x + deltaMax, midp.y - deltaMax));

        List<Tri> triangulos = new List<Tri> { CriarTri(i0, i1, i2, pts) };

        for (int p = 0; p < n; p++)
        {
            Vector2 ponto = pts[p];
            List<(int a, int b)> arestas = new List<(int, int)>();
            for (int t = triangulos.Count - 1; t >= 0; t--)
            {
                Tri tri = triangulos[t];
                float d2 = (ponto - tri.centro).sqrMagnitude;
                if (d2 <= tri.raio2)
                {
                    arestas.Add((tri.a, tri.b));
                    arestas.Add((tri.b, tri.c));
                    arestas.Add((tri.c, tri.a));
                    triangulos.RemoveAt(t);
                }
            }

            // remove arestas duplicadas (compartilhadas por dois triângulos removidos)
            List<(int a, int b)> arestasUnicas = new List<(int, int)>();
            for (int e1 = 0; e1 < arestas.Count; e1++)
            {
                bool duplicada = false;
                for (int e2 = 0; e2 < arestas.Count; e2++)
                {
                    if (e1 == e2) continue;
                    if ((arestas[e1].a == arestas[e2].a && arestas[e1].b == arestas[e2].b) ||
                        (arestas[e1].a == arestas[e2].b && arestas[e1].b == arestas[e2].a))
                    {
                        duplicada = true; break;
                    }
                }
                if (!duplicada) arestasUnicas.Add(arestas[e1]);
            }

            foreach (var e in arestasUnicas)
                triangulos.Add(CriarTri(e.a, e.b, p, pts));
        }

        // remove triângulos ligados ao super-triângulo e monta lista final
        foreach (var tri in triangulos)
        {
            if (tri.a >= n || tri.b >= n || tri.c >= n) continue;
            trianguloIndices.Add(tri.a);
            trianguloIndices.Add(tri.b);
            trianguloIndices.Add(tri.c);
        }
    }

    Tri CriarTri(int a, int b, int c, List<Vector2> pts)
    {
        Vector2 pa = pts[a], pb = pts[b], pc = pts[c];
        float ax = pa.x, ay = pa.y, bx = pb.x, by = pb.y, cx = pc.x, cy = pc.y;
        float d = 2f * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
        Vector2 centro;
        if (Mathf.Abs(d) < 1e-6f)
        {
            centro = (pa + pb + pc) / 3f;
        }
        else
        {
            float ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
            float uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;
            centro = new Vector2(ux, uy);
        }
        float raio2 = (centro - pa).sqrMagnitude;
        return new Tri { a = a, b = b, c = c, centro = centro, raio2 = raio2 };
    }

    // -------------------------------------------------------------------------------
    // 4) CONTORNO: varre a máscara e guarda pontos de borda (pra desenhar silhueta bold)
    // -------------------------------------------------------------------------------
    void ExtrairContorno()
    {
        pontosContorno.Clear();
        int w = resolucaoTela.x, h = resolucaoTela.y;
        int passo = Mathf.Max(1, Mathf.RoundToInt(raioContorno));
        for (int y = 0; y < h; y += passo)
        {
            for (int x = 0; x < w; x += passo)
            {
                if (!mascara[x, y]) continue;
                bool borda =
                    (x > 0 && !mascara[x - 1, y]) || (x < w - 1 && !mascara[x + 1, y]) ||
                    (y > 0 && !mascara[x, y - 1]) || (y < h - 1 && !mascara[x, y + 1]);
                if (borda) pontosContorno.Add(new Vector2(x, y));
            }
        }
    }

    // -------------------------------------------------------------------------------
    // TRANSFORMAÇÃO PIXEL -> MUNDO (usa a ortho size da própria câmera pra encaixar)
    // -------------------------------------------------------------------------------
    void CalcularTransformacaoMundo()
    {
        // 1 pixel de máscara = 1 unidade de mundo. A rotação/enquadramento fica toda
        // por conta da câmera (ver ConfigurarCameraFisica), então aqui é só centralizar.
        escalaMundoPorPixel = 1f;
        origemMundo = new Vector2(-resolucaoTela.x * 0.5f, -resolucaoTela.y * 0.5f);
    }

    Vector3 PixelParaMundo(Vector2 p)
    {
        return new Vector3(origemMundo.x + p.x * escalaMundoPorPixel, origemMundo.y + p.y * escalaMundoPorPixel, 0f);
    }

    // -------------------------------------------------------------------------------
    // DESENHO
    // -------------------------------------------------------------------------------
    void OnPostRender()
    {
        if (matLinha == null) return;

        GL.Clear(true, true, corFundo);
        matLinha.SetPass(0);

        // malha fina de triângulos
        GL.Begin(GL.LINES);
        GL.Color(corLinhas);
        for (int i = 0; i < trianguloIndices.Count; i += 3)
        {
            Vector3 a = PixelParaMundo(pontos[trianguloIndices[i]]);
            Vector3 b = PixelParaMundo(pontos[trianguloIndices[i + 1]]);
            Vector3 c = PixelParaMundo(pontos[trianguloIndices[i + 2]]);
            GL.Vertex(a); GL.Vertex(b);
            GL.Vertex(b); GL.Vertex(c);
            GL.Vertex(c); GL.Vertex(a);
        }
        GL.End();

        // contorno grosso das letras (desenhado como pontos "quadradinhos" pra parecer traço bold)
        GL.Begin(GL.QUADS);
        GL.Color(corContorno);
        float meia = espessuraContorno * escalaMundoPorPixel * 0.5f;
        foreach (var p in pontosContorno)
        {
            Vector3 centro = PixelParaMundo(p);
            GL.Vertex(centro + new Vector3(-meia, -meia, 0));
            GL.Vertex(centro + new Vector3(meia, -meia, 0));
            GL.Vertex(centro + new Vector3(meia, meia, 0));
            GL.Vertex(centro + new Vector3(-meia, meia, 0));
        }
        GL.End();
    }

    void OnDestroy()
    {
        if (matLinha != null) Destroy(matLinha);
    }
}