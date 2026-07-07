using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Teia de fundo animada, cobrindo a tela inteira, bem transparente mas
// ainda visível. Anexar direto numa Camera (de preferência a câmera de
// fundo/atrás da UI) — desenha com GL no OnPostRender, mesmo estilo da
// teia usada em tela1.cs. NUNCA dispersa/sai da tela (ela precisa continuar
// cobrindo a tela inteira o tempo todo, inclusive enquanto as palavras estão
// voando em tela1.cs) — em vez disso, reage ao assopro no microfone vibrando
// (tremor rápido e pequeno no lugar) e brilhando mais forte, voltando ao
// normal aos poucos quando para de soprar.
[RequireComponent(typeof(Camera))]
public class teiabg : MonoBehaviour
{
    [Header("Cor e transparência")]
    [SerializeField] private Color _corTeia = new Color(160f / 255f, 119f / 255f, 82f / 255f); // #A07752
    [SerializeField, Range(0f, 1f)] private float _opacidadeMaxima = 1f; // 100% — cor cheia, sem lavar

    [Header("Forma da teia")]
    [SerializeField] private int   _quantidadePontos = 500;  // densidade — cobre a tela inteira
    [SerializeField] private float _profundidade      = 10f; // distância à frente da câmera onde a teia "vive"
    [SerializeField] private float _margemCobertura   = 1.15f; // um pouco maior que a tela, garante cobrir até a borda
    [SerializeField] private float _espessuraLinha    = 0.21f; // espessura das linhas (+40% sobre 0.15)
    [SerializeField] private float _velocidadeAnimacao = 1f;   // multiplica a velocidade de todo o movimento (1 = normal, 2 = 2x mais rápido, 0.5 = mais lento)

    // animação: cada ponto gira depressa num círculo próprio (órbita),
    // por cima disso passam duas ondas cruzadas (uma horizontal, outra
    // vertical, velocidades diferentes) atravessando a teia inteira, mais
    // um flutuar orgânico (ruído Perlin, não se repete igual) e a teia toda
    // ainda "respira" (pulsa) — a soma disso tudo dá bastante vida ao invés
    // de só um tremor no lugar.
    private const float ONDA_VELOCIDADE      = 0.7f;
    private const float ONDA_AMPLITUDE       = 0.16f;
    private const float ONDA_COMPRIMENTO     = 0.6f;
    private const float ORBITA_RAIO_MIN      = 0.08f;
    private const float ORBITA_RAIO_MAX      = 0.35f;
    private const float ORBITA_VELOCIDADE_MIN = 0.25f;
    private const float ORBITA_VELOCIDADE_MAX = 0.75f;
    private const float FLUTUAR_VELOCIDADE    = 0.6f;  // velocidade do ruído Perlin (flutuar orgânico extra)
    private const float FLUTUAR_AMPLITUDE     = 0.08f;
    private const float RESPIRACAO_VELOCIDADE = 0.35f;
    private const float RESPIRACAO_FORCA      = 0.035f;
    private const float BRILHO_VELOCIDADE     = 1.6f;
    private const float BRILHO_FORCA          = 0.18f;

    // ── reação ao assopro — em vez de dispersar/sumir ou de aumentar o
    // deslocamento da órbita/onda (o que fazia a teia "andar" muito pela
    // tela), a teia só VIBRA no lugar: um tremor rápido e pequeno somado por
    // cima da animação normal, mais brilho — os pontos não saem muito de
    // onde já estavam. A intensidade sobe suave enquanto assopra e desce
    // suave quando para, sem nunca deixar de cobrir a tela inteira.
    [Header("Sensibilidade do microfone (sopro)")]
    [SerializeField] private float SOPRO_LIMIAR_MINIMO = 0.055f; // volume a partir do qual a teia já começa a reagir (igual ao limiar de palavras do tela1.cs)
    [SerializeField] private float SOPRO_LIMIAR_MAXIMO = 0.17f;  // volume a partir do qual a reação já está no máximo
    private const float INTENSIDADE_SUAVIZACAO   = 1.5f; // o quanto a intensidade pode subir/descer por segundo

    private const float VIBRACAO_FREQUENCIA     = 22f;   // quão rápido é o tremor (mais alto = mais "zumbido")
    private const float VIBRACAO_AMPLITUDE_MAX  = 0.05f; // deslocamento máx. do tremor no pico da intensidade — pequeno de propósito
    private const float INTENSIDADE_BRILHO_MULT = 2f;    // brilho pulsa até 3x mais forte no pico
    private const float INTENSIDADE_ALPHA_EXTRA = 0.4f;  // aumenta a opacidade máxima no pico

    private float _intensidade; // 0 (parada) .. 1 (assoprando com força), suavizada

    private Camera   _cam;
    private Material _mat;

    private Vector2[] _basePos;
    private Vector2[] _pos;
    private float[]   _phase;
    private float[]   _orbitaRaio;
    private float[]   _orbitaVelocidade;

    private int[] _edgeA;
    private int[] _edgeB;

    private float _halfWidth;
    private float _halfHeight;
    private float _alphaDist;    // distância (calculada a partir da densidade) usada p/ esmaecer linhas longas
    private float _tempoAnimado; // acumula Time.deltaTime * _velocidadeAnimacao — evita pulos se a velocidade mudar em runtime

    // não abre gravação própria de microfone: tela1.cs está na mesma câmera e
    // já grava do microfone — Unity não suporta dois Microphone.Start()
    // simultâneos no mesmo dispositivo (o segundo Start() reinicia/rouba o do
    // primeiro), então aqui só lemos o volume já suavizado por tela1 (tela1.VolumeAtual).

    void Start()
    {
        _cam = GetComponent<Camera>();

        _mat = new Material(Shader.Find("Hidden/Internal-Colored"));
        _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        _mat.SetInt("_ZWrite",   0);

        AtualizarDimensoesTela();
        GerarPontos();
        ConstruirDelaunay();
    }

    // calcula meia-largura/meia-altura da vista da câmera na profundidade
    // escolhida — assim a teia cobre a tela inteira, seja a câmera
    // ortográfica ou em perspectiva.
    void AtualizarDimensoesTela()
    {
        _halfHeight = _cam.orthographic
            ? _cam.orthographicSize
            : _profundidade * Mathf.Tan(_cam.fieldOfView * 0.5f * Mathf.Deg2Rad);

        _halfWidth = _halfHeight * _cam.aspect;
    }

    void GerarPontos()
    {
        _basePos          = new Vector2[_quantidadePontos];
        _pos              = new Vector2[_quantidadePontos];
        _phase            = new float[_quantidadePontos];
        _orbitaRaio       = new float[_quantidadePontos];
        _orbitaVelocidade = new float[_quantidadePontos];

        float w = _halfWidth  * _margemCobertura;
        float h = _halfHeight * _margemCobertura;

        for (int i = 0; i < _quantidadePontos; i++)
        {
            _basePos[i]          = new Vector2(Random.Range(-w, w), Random.Range(-h, h));
            _pos[i]              = _basePos[i];
            _phase[i]            = Random.Range(0f, Mathf.PI * 2f);
            _orbitaRaio[i]       = Random.Range(ORBITA_RAIO_MIN, ORBITA_RAIO_MAX);
            _orbitaVelocidade[i] = Random.Range(ORBITA_VELOCIDADE_MIN, ORBITA_VELOCIDADE_MAX) * (Random.value < 0.5f ? -1f : 1f);
        }

        // distância média entre pontos vizinhos — usada pra esmaecer linhas
        // compridas; assim o visual fica igual não importa a densidade/tamanho de tela.
        float area = (2f * w) * (2f * h);
        float espacamentoMedio = Mathf.Sqrt(area / Mathf.Max(1, _quantidadePontos));
        _alphaDist = espacamentoMedio * 4f;
    }

    void ConstruirDelaunay()
    {
        var triangulos    = Delaunay.Triangular(_basePos.ToList());
        var arestasUnicas = new HashSet<(int, int)>();

        foreach (var t in triangulos)
        {
            AdicionarAresta(arestasUnicas, t.a, t.b);
            AdicionarAresta(arestasUnicas, t.b, t.c);
            AdicionarAresta(arestasUnicas, t.c, t.a);
        }

        _edgeA = arestasUnicas.Select(e => e.Item1).ToArray();
        _edgeB = arestasUnicas.Select(e => e.Item2).ToArray();
    }

    void AdicionarAresta(HashSet<(int, int)> set, int i, int j) => set.Add(i < j ? (i, j) : (j, i));

    void Update()
    {
        AtualizarIntensidade();
        _tempoAnimado += Time.deltaTime * _velocidadeAnimacao;

        AtualizarOscilacao();
    }

    // lê o volume já suavizado por tela1.cs (mesma câmera, uma única gravação
    // de microfone compartilhada — ver comentário nos campos) e converte num
    // 0..1 suavizado que dá a intensidade da reação da teia ao assopro.
    void AtualizarIntensidade()
    {
        float alvo = Mathf.InverseLerp(SOPRO_LIMIAR_MINIMO, SOPRO_LIMIAR_MAXIMO, tela1.VolumeAtual);
        _intensidade = Mathf.MoveTowards(_intensidade, alvo, INTENSIDADE_SUAVIZACAO * Time.deltaTime);
    }

    // órbita + ondas cruzadas + flutuar orgânico + respiração global (ver
    // comentário lá em cima das consts) — essa parte não muda com o sopro.
    // Por cima disso, assoprar soma só um tremor rápido e pequeno (vibração),
    // sem aumentar o deslocamento da órbita/onda — a teia treme no lugar, não
    // sai andando pela tela.
    void AtualizarOscilacao()
    {
        float t = _tempoAnimado;
        float respiracao = 1f + Mathf.Sin(t * RESPIRACAO_VELOCIDADE) * RESPIRACAO_FORCA;
        float tempoRuido = t * FLUTUAR_VELOCIDADE;
        float vibAmp     = VIBRACAO_AMPLITUDE_MAX * _intensidade;

        for (int i = 0; i < _quantidadePontos; i++)
        {
            // órbita: cada ponto gira num círculo próprio
            float anguloOrbita = t * _orbitaVelocidade[i] + _phase[i];
            float ox = Mathf.Cos(anguloOrbita) * _orbitaRaio[i];
            float oy = Mathf.Sin(anguloOrbita) * _orbitaRaio[i];

            // duas ondas cruzadas percorrendo a teia inteira, em direções diferentes
            float ondaX = Mathf.Sin(t * ONDA_VELOCIDADE        - _basePos[i].y * ONDA_COMPRIMENTO) * ONDA_AMPLITUDE;
            float ondaY = Mathf.Cos(t * ONDA_VELOCIDADE * 0.8f - _basePos[i].x * ONDA_COMPRIMENTO) * ONDA_AMPLITUDE;

            // flutuar orgânico extra (ruído Perlin, contínuo mas nunca se repete igual)
            float fx = (Mathf.PerlinNoise(_phase[i], tempoRuido) - 0.5f) * 2f * FLUTUAR_AMPLITUDE;
            float fy = (Mathf.PerlinNoise(_phase[i] + 37.1f, tempoRuido) - 0.5f) * 2f * FLUTUAR_AMPLITUDE;

            // tremor rápido (vibração) — só aparece/cresce enquanto assopra
            float vx = Mathf.Sin(t * VIBRACAO_FREQUENCIA        + _phase[i] * 13.7f) * vibAmp;
            float vy = Mathf.Cos(t * VIBRACAO_FREQUENCIA * 1.3f + _phase[i] * 9.1f)  * vibAmp;

            _pos[i] = _basePos[i] * respiracao + new Vector2(ox + ondaX + fx + vx, oy + ondaY + fy + vy);
        }
    }

    void OnPostRender()
    {
        if (_edgeA == null) return;

        _mat.SetPass(0);
        // desenha cada linha como um quad (não GL.LINES) pra poder controlar
        // a espessura de verdade — GL.LINES ignora largura na maioria das
        // plataformas e sempre sai com 1px.
        GL.Begin(GL.QUADS);

        float meiaEspessura  = _espessuraLinha * 0.5f;
        float opacidadeAtual = Mathf.Clamp01(_opacidadeMaxima + _intensidade * INTENSIDADE_ALPHA_EXTRA);
        float brilhoForca    = BRILHO_FORCA * (1f + _intensidade * INTENSIDADE_BRILHO_MULT);

        for (int e = 0; e < _edgeA.Length; e++)
        {
            Vector2 pa2 = _pos[_edgeA[e]];
            Vector2 pb2 = _pos[_edgeB[e]];

            float dist  = Vector2.Distance(pa2, pb2);
            float alpha = Mathf.Clamp01(Mathf.Lerp(1f, 0f, dist / _alphaDist)) * opacidadeAtual;
            if (alpha <= 0.002f) continue;

            float brilho = 1f - brilhoForca + brilhoForca * Mathf.Sin(_tempoAnimado * BRILHO_VELOCIDADE + e * 0.37f);
            alpha *= brilho;

            Vector2 dir  = dist > 0.0001f ? (pb2 - pa2) / dist : Vector2.right;
            Vector2 perp = new Vector2(-dir.y, dir.x) * meiaEspessura;

            Vector3 p1 = _cam.transform.TransformPoint(new Vector3(pa2.x + perp.x, pa2.y + perp.y, _profundidade));
            Vector3 p2 = _cam.transform.TransformPoint(new Vector3(pb2.x + perp.x, pb2.y + perp.y, _profundidade));
            Vector3 p3 = _cam.transform.TransformPoint(new Vector3(pb2.x - perp.x, pb2.y - perp.y, _profundidade));
            Vector3 p4 = _cam.transform.TransformPoint(new Vector3(pa2.x - perp.x, pa2.y - perp.y, _profundidade));

            GL.Color(new Color(_corTeia.r, _corTeia.g, _corTeia.b, alpha));
            GL.Vertex3(p1.x, p1.y, p1.z);
            GL.Vertex3(p2.x, p2.y, p2.z);
            GL.Vertex3(p3.x, p3.y, p3.z);
            GL.Vertex3(p4.x, p4.y, p4.z);
        }

        GL.End();
    }

    void OnDestroy()
    {
        if (_mat != null) Destroy(_mat);
    }

    // =====================================================================
    // Triangulação de Delaunay (Bowyer–Watson), auto-contida — mesma lógica
    // usada em tela1.cs, copiada aqui pra este script não depender daquele.
    // =====================================================================
    static class Delaunay
    {
        public struct Triangulo
        {
            public int a, b, c;
            public Triangulo(int a, int b, int c) { this.a = a; this.b = b; this.c = c; }
        }

        struct Circuncentro
        {
            public Vector2 centro;
            public float raioQuadrado;
        }

        public static List<Triangulo> Triangular(List<Vector2> pontosOriginais)
        {
            List<Vector2> pts = new List<Vector2>(pontosOriginais);

            float minX = pts.Min(p => p.x), maxX = pts.Max(p => p.x);
            float minY = pts.Min(p => p.y), maxY = pts.Max(p => p.y);
            float dx = maxX - minX, dy = maxY - minY;
            float deltaMax = Mathf.Max(dx, dy) * 10f + 10f;
            float midX = (minX + maxX) * 0.5f, midY = (minY + maxY) * 0.5f;

            int i0 = pts.Count, i1 = pts.Count + 1, i2 = pts.Count + 2;
            pts.Add(new Vector2(midX - 2 * deltaMax, midY - deltaMax));
            pts.Add(new Vector2(midX, midY + 2 * deltaMax));
            pts.Add(new Vector2(midX + 2 * deltaMax, midY - deltaMax));

            List<Triangulo> triangulos = new List<Triangulo> { new Triangulo(i0, i1, i2) };

            for (int p = 0; p < pontosOriginais.Count; p++)
            {
                Vector2 ponto = pts[p];
                List<Triangulo> ruins = new List<Triangulo>();

                foreach (var tri in triangulos)
                {
                    var cc = CalcularCircuncentro(pts[tri.a], pts[tri.b], pts[tri.c]);
                    float distQuad = (ponto - cc.centro).sqrMagnitude;
                    if (distQuad < cc.raioQuadrado)
                        ruins.Add(tri);
                }

                List<(int, int)> arestasBuraco = new List<(int, int)>();
                foreach (var tri in ruins)
                {
                    AdicionarArestaSeUnica(arestasBuraco, ruins, tri.a, tri.b);
                    AdicionarArestaSeUnica(arestasBuraco, ruins, tri.b, tri.c);
                    AdicionarArestaSeUnica(arestasBuraco, ruins, tri.c, tri.a);
                }

                triangulos.RemoveAll(t => ruins.Contains(t));

                foreach (var (e1, e2) in arestasBuraco)
                    triangulos.Add(new Triangulo(e1, e2, p));
            }

            triangulos.RemoveAll(t =>
                t.a >= pontosOriginais.Count || t.b >= pontosOriginais.Count || t.c >= pontosOriginais.Count);

            return triangulos;
        }

        static void AdicionarArestaSeUnica(List<(int, int)> destino, List<Triangulo> ruins, int x, int y)
        {
            int contagem = 0;
            foreach (var tri in ruins)
                if (TemAresta(tri, x, y)) contagem++;

            if (contagem == 1)
                destino.Add((x, y));
        }

        static bool TemAresta(Triangulo t, int x, int y)
        {
            return (t.a == x && t.b == y) || (t.b == x && t.a == y) ||
                   (t.b == x && t.c == y) || (t.c == x && t.b == y) ||
                   (t.c == x && t.a == y) || (t.a == x && t.c == y);
        }

        static Circuncentro CalcularCircuncentro(Vector2 a, Vector2 b, Vector2 c)
        {
            float ax = a.x, ay = a.y, bx = b.x, by = b.y, cx = c.x, cy = c.y;

            float d = 2f * (ax * (by - cy) + bx * (cy - ay) + cx * (ay - by));
            if (Mathf.Abs(d) < 1e-9f)
                return new Circuncentro { centro = a, raioQuadrado = float.MaxValue };

            float ux = ((ax * ax + ay * ay) * (by - cy) + (bx * bx + by * by) * (cy - ay) + (cx * cx + cy * cy) * (ay - by)) / d;
            float uy = ((ax * ax + ay * ay) * (cx - bx) + (bx * bx + by * by) * (ax - cx) + (cx * cx + cy * cy) * (bx - ax)) / d;

            Vector2 centro = new Vector2(ux, uy);
            return new Circuncentro { centro = centro, raioQuadrado = (centro - a).sqrMagnitude };
        }
    }
}
