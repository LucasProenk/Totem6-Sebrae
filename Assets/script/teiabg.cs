using System.Collections.Generic;
using System.Linq;
using UnityEngine;

// Teia de fundo animada, cobrindo a tela inteira, bem transparente mas
// ainda visível. Anexar direto numa Camera (de preferência a câmera de
// fundo/atrás da UI) — desenha com GL no OnPostRender, mesmo estilo da
// teia usada em tela1.cs. Também usa o mesmo padrão de tela1.cs pra
// assopro no microfone: assoprou, a teia se espalha; parou de assoprar
// (inatividade), depois de um tempo ela volta se reconectando sozinha.
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

    // ── assopro no microfone — mesmo padrão de tela1.cs: sopro confirmado
    // (sustentado, evita disparar à toa com ruído) espalha a teia; enquanto
    // a pessoa continuar soprando (mesmo levinho) a teia continua espalhada;
    // depois de um tempo de silêncio (inatividade) ela reconecta sozinha.
    private const float SOPRO_LIMIAR_INICIO   = 0.035f; // volume exigido pra DISPARAR a dispersão
    private const float SOPRO_LIMIAR_CONTINUO = 0.011f; // volume (bem mais sensível) que mantém a teia espalhada
    private const float SOPRO_MIN_DURACAO          = 0.18f; // segundos seguidos acima do limiar de início pra confirmar sopro de verdade
    private const float SOPRO_MIN_DURACAO_CONTINUO = 0.10f; // idem, pro limiar contínuo
    private const float RECONSTRUIR_TIMEOUT  = 4f;    // segundos de silêncio no mic antes de reconectar a teia

    private const float DISPERSAO_VEL_MIN = 3f;
    private const float DISPERSAO_VEL_MAX = 7f;
    private const float DISPERSAO_ACEL    = 6f; // aceleração radial (efeito de "explosão")

    private const float RECONEXAO_ATRASO_MAX    = 1.0f; // atraso aleatório máx. antes de cada ponto começar a voltar
    private const float RECONEXAO_DURACAO_PONTO = 1.3f; // tempo que cada ponto leva pra viajar até o lugar final

    enum Estado { Normal, Dispersando, Reconectando }
    private Estado _estado = Estado.Normal;

    private Camera   _cam;
    private Material _mat;

    private Vector2[] _basePos;
    private Vector2[] _pos;
    private Vector2[] _vel;
    private float[]   _phase;
    private float[]   _orbitaRaio;
    private float[]   _orbitaVelocidade;

    private Vector2[] _posOrigemReconexao;
    private float[]   _atrasoReconexao;
    private float     _reconexaoTimer;

    private int[] _edgeA;
    private int[] _edgeB;

    private float _halfWidth;
    private float _halfHeight;
    private float _alphaDist;    // distância (calculada a partir da densidade) usada p/ esmaecer linhas longas
    private float _tempoAnimado; // acumula Time.deltaTime * _velocidadeAnimacao — evita pulos se a velocidade mudar em runtime

    // ── microfone / detecção de sopro ────────────────────────────────────
    private string    _micDevice;
    private AudioClip _micClip;
    private float[]   _micBuffer = new float[1024];
    private float     _volumeSuavizado;
    private float     _ultimoSoproTime = -999f;
    private float     _tempoAcimaDoLimiar;
    private float     _tempoAcimaLimiarContinuo;
    private bool      _soproConfirmado;

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
        IniciarMicrofone();
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
        _vel              = new Vector2[_quantidadePontos];
        _phase            = new float[_quantidadePontos];
        _orbitaRaio       = new float[_quantidadePontos];
        _orbitaVelocidade = new float[_quantidadePontos];
        _posOrigemReconexao = new Vector2[_quantidadePontos];
        _atrasoReconexao    = new float[_quantidadePontos];

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
        _tempoAnimado += Time.deltaTime * _velocidadeAnimacao;

        AtualizarMicrofone();

        switch (_estado)
        {
            case Estado.Normal:
                AtualizarOscilacaoNormal();
                if (_soproConfirmado) IniciarDispersao();
                break;

            case Estado.Dispersando:
                AtualizarDispersao();
                if (!DentroDoTimeoutReconstrucao()) IniciarReconexao();
                break;

            case Estado.Reconectando:
                AtualizarReconexao();
                break;
        }
    }

    // teia parada (estado de repouso): órbita + ondas cruzadas + flutuar
    // orgânico + respiração global (ver comentário lá em cima das consts).
    void AtualizarOscilacaoNormal()
    {
        float t = _tempoAnimado;
        float respiracao = 1f + Mathf.Sin(t * RESPIRACAO_VELOCIDADE) * RESPIRACAO_FORCA;
        float tempoRuido = t * FLUTUAR_VELOCIDADE;

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

            _pos[i] = _basePos[i] * respiracao + new Vector2(ox + ondaX + fx, oy + ondaY + fy);
        }
    }

    // =====================================================================
    // MICROFONE — mesmo padrão de tela1.cs (primeiro dispositivo conectado).
    // =====================================================================
    void IniciarMicrofone()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("teiabg: nenhum microfone conectado.");
            return;
        }

        _micDevice = Microphone.devices[0];
        _micClip   = Microphone.Start(_micDevice, true, 1, 44100);
    }

    void AtualizarMicrofone()
    {
        if (_micClip == null) return;

        int posicao = Microphone.GetPosition(_micDevice);
        int inicio  = posicao - _micBuffer.Length;
        if (inicio < 0) return; // ainda não há amostras suficientes

        _micClip.GetData(_micBuffer, inicio);

        float somaQuadrados = 0f;
        for (int i = 0; i < _micBuffer.Length; i++)
            somaQuadrados += _micBuffer[i] * _micBuffer[i];

        float volume = Mathf.Sqrt(somaQuadrados / _micBuffer.Length);
        _volumeSuavizado = Mathf.Lerp(_volumeSuavizado, volume, Time.deltaTime * 12f);

        // limiar baixo (sopro leve) mantém a teia espalhada enquanto durar —
        // mas precisa ficar sustentado um instante, senão ruído já conta.
        if (_volumeSuavizado > SOPRO_LIMIAR_CONTINUO)
            _tempoAcimaLimiarContinuo += Time.deltaTime;
        else
            _tempoAcimaLimiarContinuo = 0f;

        if (_tempoAcimaLimiarContinuo >= SOPRO_MIN_DURACAO_CONTINUO)
            _ultimoSoproTime = Time.time;

        // limiar alto (sopro forte e sustentado) só conta pra DISPARAR a dispersão inicial.
        if (_volumeSuavizado > SOPRO_LIMIAR_INICIO)
            _tempoAcimaDoLimiar += Time.deltaTime;
        else
            _tempoAcimaDoLimiar = 0f;

        _soproConfirmado = _tempoAcimaDoLimiar >= SOPRO_MIN_DURACAO;
    }

    // true enquanto a pessoa ainda está soprando OU faz menos de RECONSTRUIR_TIMEOUT
    // segundos que parou — só depois disso (inatividade) a teia reconecta.
    bool DentroDoTimeoutReconstrucao()
    {
        return (Time.time - _ultimoSoproTime) < RECONSTRUIR_TIMEOUT;
    }

    // =====================================================================
    // DISPERSÃO — sopro confirmado: cada ponto ganha uma velocidade radial
    // (pra fora do centro) que vai acelerando, como se a teia explodisse
    // e saísse voando da tela. Mesma física de tela1.cs.
    // =====================================================================
    void IniciarDispersao()
    {
        _estado = Estado.Dispersando;

        for (int i = 0; i < _quantidadePontos; i++)
        {
            Vector2 baseDir = _pos[i].sqrMagnitude > 0.0001f ? _pos[i].normalized : Random.insideUnitCircle.normalized;
            float   desvio  = Random.Range(-25f, 25f);
            Vector2 dir     = Quaternion.Euler(0f, 0f, desvio) * baseDir;
            _vel[i] = dir * Random.Range(DISPERSAO_VEL_MIN, DISPERSAO_VEL_MAX);
        }
    }

    void AtualizarDispersao()
    {
        for (int i = 0; i < _quantidadePontos; i++)
        {
            Vector2 dir = _vel[i].normalized;
            _vel[i] += dir * DISPERSAO_ACEL * Time.deltaTime;
            _pos[i] += _vel[i] * Time.deltaTime;
        }
    }

    // =====================================================================
    // RECONEXÃO — a teia volta de onde os pontos estão (espalhados pela
    // dispersão) até o layout original, cada ponto com um atraso aleatório e
    // chegando aos poucos, "costurando" a teia de novo. Mesma lógica de tela1.cs.
    // =====================================================================
    void IniciarReconexao()
    {
        for (int i = 0; i < _quantidadePontos; i++)
        {
            _posOrigemReconexao[i] = _pos[i];
            _atrasoReconexao[i]    = Random.Range(0f, RECONEXAO_ATRASO_MAX);
        }

        _reconexaoTimer = 0f;
        _estado = Estado.Reconectando;
    }

    void AtualizarReconexao()
    {
        _reconexaoTimer += Time.deltaTime;

        bool todasChegaram = true;
        for (int i = 0; i < _quantidadePontos; i++)
        {
            float tLocal = Mathf.Clamp01((_reconexaoTimer - _atrasoReconexao[i]) / RECONEXAO_DURACAO_PONTO);
            if (tLocal < 1f) todasChegaram = false;

            float suave = 1f - (1f - tLocal) * (1f - tLocal); // ease-out: desacelera ao chegar no lugar
            _pos[i] = Vector2.Lerp(_posOrigemReconexao[i], _basePos[i], suave);
        }

        if (todasChegaram) _estado = Estado.Normal;
    }

    void OnPostRender()
    {
        if (_edgeA == null) return;

        _mat.SetPass(0);
        // desenha cada linha como um quad (não GL.LINES) pra poder controlar
        // a espessura de verdade — GL.LINES ignora largura na maioria das
        // plataformas e sempre sai com 1px.
        GL.Begin(GL.QUADS);

        float meiaEspessura = _espessuraLinha * 0.5f;

        for (int e = 0; e < _edgeA.Length; e++)
        {
            Vector2 pa2 = _pos[_edgeA[e]];
            Vector2 pb2 = _pos[_edgeB[e]];

            float dist  = Vector2.Distance(pa2, pb2);
            float alpha = Mathf.Clamp01(Mathf.Lerp(1f, 0f, dist / _alphaDist)) * _opacidadeMaxima;
            if (alpha <= 0.002f) continue;

            float brilho = 1f - BRILHO_FORCA + BRILHO_FORCA * Mathf.Sin(_tempoAnimado * BRILHO_VELOCIDADE + e * 0.37f);
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
        if (_micDevice != null && Microphone.IsRecording(_micDevice)) Microphone.End(_micDevice);
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
