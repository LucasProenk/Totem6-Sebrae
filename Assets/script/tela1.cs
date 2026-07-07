using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

[RequireComponent(typeof(Camera))]
public class tela1 : MonoBehaviour
{
    [Header("Aparência da teia")]
    [SerializeField] private float _teiaEspessura = 0.05f; // espessura das linhas da malha principal (unidades de conteúdo)

    private Material _mat;
    private Camera   _cam;
    private Vector2[] _basePos;
    private Vector2[] _pos;
    private Vector2[] _vel;
    private float[]   _freq;
    private float[]   _phase;

    private const int   COUNT         = 1300;  // densidade total de pontos da teia
    private const float ALPHA_DIST    = 2.0f;  // distância usada para esmaecer linhas longas
    private const float TEIA_ESCALA   = 0.72f; // encolhe a teia toda (mais 10% em cima dos 20% já aplicados) pra nada ficar cortado fora da tela
    private const float ROTATION_Z    = 90f;   // tela física rotacionada (mesmo sentido do texto)
    private const float CONTENT_W     = 17.0f; // largura total ocupada pelas letras (eixo x local)
    private const float CONTENT_H     = 9.6f;  // altura total ocupada pelas letras (eixo y local)

    // animação da teia parada (estado Normal): tremor individual de cada ponto
    // + uma onda lenta que percorre a malha inteira da esquerda pra direita +
    // um brilho cintilante nas linhas, tudo somado pra dar vida à teia.
    private const float TEIA_JITTER_AMPLITUDE = 0.06f;
    private const float TEIA_ONDA_VELOCIDADE  = 0.6f;  // velocidade com que a onda percorre a teia
    private const float TEIA_ONDA_AMPLITUDE   = 0.09f; // o quanto a onda desloca os pontos
    private const float TEIA_ONDA_COMPRIMENTO = 0.45f; // "comprimento de onda" ao longo do eixo x
    private const float TEIA_BRILHO_VELOCIDADE = 1.4f; // velocidade da cintilação das linhas
    private const float TEIA_BRILHO_FORCA      = 0.25f; // o quanto o brilho de cada linha varia (0..1)

    // linhas finas que "puxam" dos pontos mais externos da teia até a borda —
    // dá a sensação da teia se conectando/puxando o texto de fundo por trás.
    private const int   CONEXAO_QTD    = 18;
    private const float CONEXAO_ALCANCE = 5.5f;  // o quanto a linha se estica além do ponto
    private const float CONEXAO_ALPHA   = 0.3f;

    // arestas da triangulação de Delaunay (calculadas 1x no Start)
    private int[] _edgeA;
    private int[] _edgeB;

    // ── estado geral (sopro → teia se espalha (já soltando palavras) → palavras → reconecta) ──
    enum Estado { Normal, Dispersando, Palavras, Reconectando }
    private Estado _estado = Estado.Normal;

    // ── reconexão da teia (volta de onde os pontos estão espalhados até o
    // lugar final, cada um com um atraso aleatório — "costura" a teia de novo
    // em vez de simplesmente reaparecer pronta) ──────────────────────────────
    private Vector2[] _posOrigemReconexao;
    private float[]   _atrasoReconexao;
    private float     _reconexaoTimer;

    private const float RECONEXAO_ATRASO_MAX    = 1.0f; // atraso aleatório máx. antes de cada ponto começar a voltar
    private const float RECONEXAO_DURACAO_PONTO = 1.3f; // tempo que cada ponto leva pra viajar até o lugar final

    // ── microfone / detecção de sopro ────────────────────────────────────
    // static porque teiabg.cs está na mesma câmera e reaproveita esta MESMA
    // gravação (Unity não suporta dois Microphone.Start() simultâneos no
    // mesmo dispositivo — o segundo Start() reinicia/rouba o do primeiro).
    private string  _micDevice;
    private AudioClip _micClip;
    private float[] _micBuffer = new float[1024];
    private static float _volumeSuavizado;
    private float   _ultimoSoproTime = -999f;
    private float   _tempoAcimaDoLimiar;         // quanto tempo seguido o volume está acima do limiar de início
    private float   _tempoAcimaLimiarPalavras;   // quanto tempo seguido o volume está acima do limiar (leve) das palavras
    private bool    _soprando;
    private bool    _soproConfirmado; // só vira true depois de um sopro sustentado (filtra ruído curto)

    // força do sopro (0..1), suavizada com o tempo: não pula direto pro valor
    // novo quando o volume muda — vai subindo aos poucos enquanto o sopro
    // continua forte, e descendo aos poucos quando ele afrouxa. É essa suavização
    // que dá a sensação leve/suave em vez de tranco quando o sopro varia.
    private float   _forcaSopro;
    private const float FORCA_SOPRO_SUAVIZACAO = 0.55f; // o quanto _forcaSopro pode mudar por segundo (menor = mais suave/lento)

    private const float SOPRO_LIMIAR_INICIO       = 0.026f; // sopro mais forte, exigido só pra DISPARAR a dispersão (evita ativar à toa)
    private const float SOPRO_LIMIAR_PALAVRAS     = 0.008f; // sopro durante as palavras — bem mais sensível, pega sopro bem leve
    private const float SOPRO_LIMIAR_FORTE        = 0.09f;  // volume a partir do qual o sopro conta como "forte" — mais palavras, voo mais longe e mais fundo
    private const float SOPRO_MIN_DURACAO         = 0.18f;  // segundos seguidos acima do limiar de início pra confirmar sopro de verdade
    private const float SOPRO_MIN_DURACAO_PALAVRAS = 0.10f; // idem, mas pro limiar das palavras — reage mais rápido a um sopro leve
    private const float SOPRO_GRACA       = 0.25f;  // segundos de tolerância antes de considerar que parou de soprar
    private const float RECONSTRUIR_TIMEOUT = 4f; // segundos de silêncio no mic, durante as palavras, antes de reconstruir a teia

    // ── dispersão "física" dos pontos da teia ────────────────────────────
    private const float DISPERSAO_VEL_MIN = 3f;
    private const float DISPERSAO_VEL_MAX = 7f;
    private const float DISPERSAO_ACEL    = 6f;  // aceleração radial (efeito de "explosão")
    private const float DISPERSAO_LIMITE  = 13f; // distância do centro a partir da qual o ponto é considerado fora da tela

    // ── fila de palavras ──────────────────────────────────────────────────
    class PalavraAtiva
    {
        public GameObject go;
        public TextMeshProUGUI tmp;
        public RectTransform rt;
        public float progresso;   // 0..1, avança conforme a velocidade atual (não é tempo fixo)
        public float velocidade;  // velocidade atual desta palavra — começa baixa e só cresce
        public float tombo;       // graus extras de giro durante o voo (varia por palavra)
        public Vector2 posInicial; // onde nasce (um pouco abaixo do centro, x aleatório)
        public Vector2 direcao;    // direção do voo, subindo pra tela (com um pequeno desvio aleatório)
        public float lentidaoLeitura; // >1 pra palavras longas, dá mais tempo de leitura
        public float forca;       // 0..1, o sopro mais forte que essa palavra já pegou — quanto maior, mais ela sobe e mais fundo ela vai
    }

    private List<string> _palavras = new List<string>();
    private List<string> _filaEmbaralhada = new List<string>();
    private int   _filaIndice;
    private List<PalavraAtiva> _palavrasAtivas = new List<PalavraAtiva>();
    private float _proximoSpawnEm;
    private Transform _canvasTransform;
    private TextMeshProUGUI _labelInstrucao;

    // eixo "pra cima" da tela física (a tela é rotacionada, então isso é um eixo
    // do canvas, não o Y normal de tela) — palavras nascem embaixo, com um
    // desvio lateral aleatório (pra não nascerem todas empilhadas quando tem
    // várias ao mesmo tempo), e sobem nesse sentido com um desvio de ângulo
    // que faz cada uma ir pro seu lado, espalhando o voo.
    private static readonly Vector2 EIXO_CIMA  = new Vector2(1f, 0f);
    private const float ANGULO_MAX_VOO = 65f;  // graus de desvio possível em torno do eixo "pra cima"
    private const float PALAVRA_SPAWN_LADO = 220f; // desvio lateral aleatório ao nascer

    // espaçamento entre um spawn e outro — sopro fraco/normal nasce mais devagar,
    // sopro forte sustentado nasce bem mais rápido (mais palavras vindo).
    private const float PALAVRA_SPAWN_COOLDOWN_FRACO = 0.3f;
    private const float PALAVRA_SPAWN_COOLDOWN_FORTE = 0.06f;

    // quantas palavras cabem na tela ao mesmo tempo — cresce com a força do sopro
    // (sopro fraco/normal = 6, sopro forte sustentado = até 14 de uma vez).
    private const int   PALAVRA_MAX_SIMULTANEAS_BASE  = 6;
    private const int   PALAVRA_MAX_SIMULTANEAS_FORTE = 14;

    private const float PALAVRA_CRESCE_FRACAO = 0.15f; // fração inicial do progresso = nascendo/crescendo
    private const float PALAVRA_FADE_FRACAO   = 0.3f;  // fração final do progresso = sumindo
    private const float PALAVRA_ESCALA_CHEGADA = 0.85f; // tamanho ao nascer
    private const float PALAVRA_ESCALA_INICIO = 1.0f;   // tamanho de pico, logo depois de nascer (mais perto da "câmera")

    // profundidade final e distância percorrida também escalam com a força do
    // sopro que a palavra pegou: sopro fraco = sobe pouco e some perto ainda
    // grande; sopro forte = sobe bem mais e afunda quase até sumir.
    private const float PALAVRA_ESCALA_FIM_FRACO  = 0.30f;
    private const float PALAVRA_ESCALA_FIM_FORTE  = 0.04f;
    private const float PALAVRA_SOBE_DIST_FRACO   = 400f;
    private const float PALAVRA_SOBE_DIST_FORTE   = 1250f;

    private const float PALAVRA_SPAWN_BAIXO   = 380f;   // o quão abaixo do centro ela nasce, em média
    private const float PALAVRA_SPAWN_BAIXO_VARIACAO = 300f; // varia esse "abaixo" bastante pra cada palavra nascer visivelmente mais pra cima ou mais pra baixo que a outra
    private const float PALAVRA_TOMBO_MAX     = 50f;    // giro extra aleatório (graus) simulando o galho tombando no vento
    private const float PALAVRA_CAIXA_LARGURA = 950f;   // largura segura da caixa de texto (cabe na tela física sem cortar)
    private const float PALAVRA_FONTE_MAX     = 90f;
    private const float PALAVRA_FONTE_MIN     = 36f;

    // cada palavra nasce devagar e vai ganhando velocidade sozinha com o tempo
    // (garante que sempre termina de sair da tela, nunca trava no meio); soprar
    // acelera ela mais rápido ainda, em cima dessa aceleração natural. A
    // velocidade final ainda é dividida pelo tamanho da palavra (palavra longa
    // precisa de mais tempo pra ler).
    private const float PALAVRA_VEL_INICIAL = 0.12f; // velocidade ao nascer — sempre começa devagar
    private const float PALAVRA_ACEL_BASE   = 0.24f; // aceleração natural (sozinha, sem depender de sopro)
    private const float PALAVRA_ACEL_SOPRO  = 2.4f;  // aceleração extra por segundo enquanto sopra
    private const float PALAVRA_VEL_MAX     = 1.0f;  // teto de velocidade, pra sempre sobrar tempo de leitura

    // enquanto a palavra ainda está "chegando" (aparecendo), ela sempre anda
    // nesse ritmo fixo, sem importar a força do sopro — assim dá tempo de
    // começar a ler antes de acelerar; sopro forte só acelera quem já está
    // no meio do caminho (depois da fase de chegada).
    private const float PALAVRA_VEL_CHEGADA = 0.3f;

    // ── máscara "PROPAGAR" — as 3 linhas (PRO / PA / GAR) são renderizadas de
    // verdade (TextMeshPro) numa textura preto-e-branco, uma única vez; os
    // pontos da teia só nascem na parte BRANCA (as letras) dessa textura —
    // a teia preenche as letras, o resto da tela fica sem pontos.
    private Color32[] _mascaraPixels;
    private int       _mascaraW, _mascaraH;

    private static readonly string[] MASCARA_LINHAS = { "PRO", "PA", "GAR" };
    private const int MASCARA_RESOLUCAO_X = 1024; // resolução da textura de máscara (largura)

    void Start()
    {
        Random.InitState(12345); // seed fixa: mantém a teia igual em toda execução

        _cam = GetComponent<Camera>();

        _mat = new Material(Shader.Find("Hidden/Internal-Colored"));
        _mat.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        _mat.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        _mat.SetInt("_Cull",     (int)UnityEngine.Rendering.CullMode.Off);
        _mat.SetInt("_ZWrite",   0);

        ConstruirMascaraLetras(); // renderiza PRO/PA/GAR numa textura, usada como máscara dos pontos

        _basePos            = new Vector2[COUNT];
        _pos                = new Vector2[COUNT];
        _vel                = new Vector2[COUNT];
        _freq               = new float[COUNT];
        _phase              = new float[COUNT];
        _posOrigemReconexao = new Vector2[COUNT];
        _atrasoReconexao    = new float[COUNT];

        GerarPontos();
        ConstruirDelaunay();
        ConstruirCanvasTexto();
        CarregarPalavras();
        IniciarMicrofone();
    }

    // =====================================================================
    // PALAVRAS — carrega a lista de Assets/Resources/palavras.txt (1 por linha)
    // =====================================================================
    void CarregarPalavras()
    {
        TextAsset asset = Resources.Load<TextAsset>("palavras");
        if (asset == null)
        {
            Debug.LogWarning("tela1: Assets/Resources/palavras.txt não encontrado.");
            return;
        }

        foreach (string linha in asset.text.Split('\n'))
        {
            string p = linha.Trim();
            if (!string.IsNullOrEmpty(p)) _palavras.Add(p);
        }
    }

    // =====================================================================
    // MICROFONE — usa o primeiro dispositivo conectado, qualquer um
    // =====================================================================
    void IniciarMicrofone()
    {
        if (Microphone.devices.Length == 0)
        {
            Debug.LogWarning("tela1: nenhum microfone conectado.");
            return;
        }

        _micDevice = Microphone.devices[0];
        _micClip   = Microphone.Start(_micDevice, true, 1, 44100);
    }

    // exposto pra teiabg.cs (mesma câmera) ler o volume já suavizado em vez
    // de abrir sua própria gravação do microfone.
    public static float VolumeAtual => _volumeSuavizado;

    void AtualizarMicrofone()
    {
        if (_micClip == null) return;

        int posicao = Microphone.GetPosition(_micDevice);
        int inicio  = posicao - _micBuffer.Length;
        if (inicio < 0) return; // ainda não há amostras suficientes (início ou voltou do laço)

        _micClip.GetData(_micBuffer, inicio);

        float somaQuadrados = 0f;
        for (int i = 0; i < _micBuffer.Length; i++)
            somaQuadrados += _micBuffer[i] * _micBuffer[i];

        float volume = Mathf.Sqrt(somaQuadrados / _micBuffer.Length);
        _volumeSuavizado = Mathf.Lerp(_volumeSuavizado, volume, Time.deltaTime * 12f);

        // limiar baixo (sopro leve) mantém "_soprando" vivo — usado durante as
        // palavras, pra não exigir fôlego forte o tempo todo. Mas precisa ficar
        // sustentado um instante, senão ruído/batida rápida já conta como sopro.
        if (_volumeSuavizado > SOPRO_LIMIAR_PALAVRAS)
            _tempoAcimaLimiarPalavras += Time.deltaTime;
        else
            _tempoAcimaLimiarPalavras = 0f;

        if (_tempoAcimaLimiarPalavras >= SOPRO_MIN_DURACAO_PALAVRAS)
            _ultimoSoproTime = Time.time;

        // limiar alto (sopro forte e sustentado) só conta pra DISPARAR a dispersão inicial.
        if (_volumeSuavizado > SOPRO_LIMIAR_INICIO)
            _tempoAcimaDoLimiar += Time.deltaTime;
        else
            _tempoAcimaDoLimiar = 0f;

        _soprando        = (Time.time - _ultimoSoproTime) < SOPRO_GRACA;
        _soproConfirmado = _tempoAcimaDoLimiar >= SOPRO_MIN_DURACAO;
    }

    // =====================================================================
    // MÁSCARA "PROPAGAR" — renderiza as 3 linhas (PRO / PA / GAR) de verdade
    // com TextMeshPro numa RenderTexture temporária (branco no preto), lê os
    // pixels de volta pra um array em memória, e destrói tudo o que foi usado
    // pra gerar essa textura. Feito 1x no Start; GerarPontos() só consulta o
    // array já pronto (MascaraEhLetra) — nenhum arquivo de imagem externo.
    // =====================================================================
    void ConstruirMascaraLetras()
    {
        int mascaraH = Mathf.RoundToInt(MASCARA_RESOLUCAO_X * CONTENT_H / CONTENT_W);

        var rt = new RenderTexture(MASCARA_RESOLUCAO_X, mascaraH, 0, RenderTextureFormat.ARGB32);

        GameObject camGO = new GameObject("MascaraLetrasCam_Temp");
        Camera cam = camGO.AddComponent<Camera>();
        cam.orthographic       = true;
        cam.orthographicSize   = CONTENT_H * 0.5f;
        cam.aspect             = CONTENT_W / CONTENT_H;
        cam.clearFlags         = CameraClearFlags.SolidColor;
        cam.backgroundColor    = Color.black;
        cam.targetTexture      = rt;
        cam.transform.position = new Vector3(0f, 0f, -10f);

        float alturaLinha = CONTENT_H / MASCARA_LINHAS.Length;
        var   textosTemp  = new List<GameObject>();

        for (int i = 0; i < MASCARA_LINHAS.Length; i++)
        {
            GameObject textoGO = new GameObject("MascaraLetrasTexto_Temp" + i);
            textoGO.transform.position = new Vector3(0f, CONTENT_H * 0.5f - alturaLinha * (i + 0.5f), 0f);

            TextMeshPro tmp = textoGO.AddComponent<TextMeshPro>();
            tmp.text              = MASCARA_LINHAS[i];
            tmp.color             = Color.white;
            tmp.fontStyle         = FontStyles.Bold;
            tmp.alignment         = TextAlignmentOptions.Center;
            tmp.rectTransform.sizeDelta = new Vector2(CONTENT_W, alturaLinha);
            tmp.enableAutoSizing  = true; // encolhe/cresce sozinho pra preencher a linha certinho
            tmp.fontSizeMin       = 1f;
            tmp.fontSizeMax       = alturaLinha * 10f;
            tmp.ForceMeshUpdate(); // calcula o auto-size já nesse frame, antes da câmera renderizar

            textosTemp.Add(textoGO);
        }

        cam.Render();

        RenderTexture anterior = RenderTexture.active;
        RenderTexture.active   = rt;
        Texture2D captura      = new Texture2D(MASCARA_RESOLUCAO_X, mascaraH, TextureFormat.RGBA32, false);
        captura.ReadPixels(new Rect(0, 0, MASCARA_RESOLUCAO_X, mascaraH), 0, 0);
        captura.Apply();
        RenderTexture.active   = anterior;

        _mascaraPixels = captura.GetPixels32();
        _mascaraW      = MASCARA_RESOLUCAO_X;
        _mascaraH      = mascaraH;

        foreach (GameObject go in textosTemp) Destroy(go);
        cam.targetTexture = null;
        Destroy(camGO);
        Destroy(captura);
        rt.Release();
        Destroy(rt);
    }

    // true se o ponto (em coordenadas de conteúdo, iguais às da teia) cair em
    // cima de uma letra na máscara — pixel branco. Fundo (preto) = false.
    bool MascaraEhLetra(float x, float y)
    {
        // a tela final gira o conteúdo em ROTATION_Z por cima deste espaço (ver
        // OnPostRender), o que deixava a teia espelhada e de ponta cabeça em
        // relação à máscara renderizada "normal" (PRO topo / PA meio / GAR
        // base, sem inversão). Girar a amostragem 180° aqui (x,y → -x,-y)
        // corrige os dois problemas de uma vez, sem precisar mexer nas
        // strings ou na ordem das linhas em ConstruirMascaraLetras().
        x = -x;
        y = -y;

        int px = Mathf.Clamp(Mathf.FloorToInt((x + CONTENT_W * 0.5f) / CONTENT_W * _mascaraW), 0, _mascaraW - 1);
        int py = Mathf.Clamp(Mathf.FloorToInt((y + CONTENT_H * 0.5f) / CONTENT_H * _mascaraH), 0, _mascaraH - 1);
        return _mascaraPixels[py * _mascaraW + px].r > 128;
    }

    // =====================================================================
    // GERAÇÃO DE PONTOS — sorteia posições por toda a área de conteúdo e só
    // aceita as que caem nas partes BRANCAS (as letras) da máscara; a teia
    // preenche as letras, o resto da tela fica sem pontos.
    // =====================================================================
    void GerarPontos()
    {
        for (int i = 0; i < COUNT; i++)
        {
            float px = 0f, py = 0f;
            for (int tentativa = 0; tentativa < 2000; tentativa++)
            {
                px = Random.Range(-CONTENT_W * 0.5f, CONTENT_W * 0.5f);
                py = Random.Range(-CONTENT_H * 0.5f, CONTENT_H * 0.5f);
                if (MascaraEhLetra(px, py)) break; // caiu numa parte branca (letra), serve
            }
            _basePos[i] = new Vector2(px, py);
        }

        for (int i = 0; i < COUNT; i++)
        {
            _freq[i]  = Random.Range(0.4f, 1.2f);
            _phase[i] = Random.Range(0f, Mathf.PI * 2f);
            _pos[i]   = _basePos[i];
        }
    }

    // =====================================================================
    // TRIANGULAÇÃO DE DELAUNAY — calculada 1x (não a cada frame!)
    // =====================================================================
    void ConstruirDelaunay()
    {
        var triangulos = Delaunay.Triangular(_basePos.ToList());

        var arestasUnicas = new HashSet<(int, int)>();
        foreach (var t in triangulos)
        {
            AdicionarAresta(arestasUnicas, t.a, t.b);
            AdicionarAresta(arestasUnicas, t.b, t.c);
            AdicionarAresta(arestasUnicas, t.c, t.a);
        }

        // Delaunay não sabe nada sobre "buracos" de letra — sem esse filtro,
        // ele conecta pontos que estão dos dois lados do vão de um O/P/R/A/G
        // (ou até de uma letra pra outra), tampando o buraco e virando uma
        // mancha sólida em vez da letra. Só mantém a aresta se o segmento
        // inteiro passar por cima de partes brancas (letra) da máscara.
        var arestasValidas = arestasUnicas.Where(e => ArestaDentroDaLetra(e.Item1, e.Item2));

        _edgeA = arestasValidas.Select(e => e.Item1).ToArray();
        _edgeB = arestasValidas.Select(e => e.Item2).ToArray();
    }

    void AdicionarAresta(HashSet<(int, int)> set, int i, int j)
    {
        set.Add(i < j ? (i, j) : (j, i));
    }

    private const int ARESTA_AMOSTRAS = 6; // pontos verificados ao longo do segmento
    bool ArestaDentroDaLetra(int i, int j)
    {
        Vector2 a = _basePos[i], b = _basePos[j];
        for (int s = 1; s < ARESTA_AMOSTRAS; s++)
        {
            Vector2 p = Vector2.Lerp(a, b, (float)s / ARESTA_AMOSTRAS);
            if (!MascaraEhLetra(p.x, p.y)) return false;
        }
        return true;
    }

    // =====================================================================
    // CANVAS UI — texto vertical "assopre para" na borda direita
    // =====================================================================
    void ConstruirCanvasTexto()
    {
        GameObject canvasGO = new GameObject("Canvas");
        Canvas canvas = canvasGO.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;

        var scaler = canvasGO.AddComponent<CanvasScaler>();
        scaler.uiScaleMode         = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920f, 1080f);
        scaler.matchWidthOrHeight  = 0.5f;

        canvasGO.AddComponent<GraphicRaycaster>();
        _canvasTransform = canvasGO.transform;

        GameObject textGO = new GameObject("Label");
        textGO.transform.SetParent(canvasGO.transform, false);

        TextMeshProUGUI tmp = textGO.AddComponent<TextMeshProUGUI>();
        tmp.text             = "assopre para";
        tmp.fontSize         = 42f;
        tmp.color            = new Color(0.227f, 0.180f, 0.122f, 1f);
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.characterSpacing = 8f;
        _labelInstrucao      = tmp;

        RectTransform rt = textGO.GetComponent<RectTransform>();
        rt.anchorMin        = new Vector2(1f, 0.5f);
        rt.anchorMax        = new Vector2(1f, 0.5f);
        rt.pivot            = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition = new Vector2(-60f, 0f);
        rt.sizeDelta        = new Vector2(900f, 80f);
        rt.localRotation    = Quaternion.Euler(0f, 0f, -90f);
    }

    void Update()
    {
        AtualizarMicrofone();

        switch (_estado)
        {
            case Estado.Normal:
                AtualizarOscilacaoNormal();
                if (_soproConfirmado) IniciarDispersao();
                break;

            case Estado.Dispersando:
                AtualizarDispersao();
                AtualizarPalavras(); // já começa a soltar palavras enquanto a teia ainda está saindo, sem esperar ela sumir de vez
                if (TodosPontosForaDaTela()) _estado = Estado.Palavras;
                break;

            case Estado.Palavras:
                AtualizarPalavras();
                if (!DentroDoTimeoutReconstrucao()) IniciarReconexao();
                break;

            case Estado.Reconectando:
                AtualizarReconexao();
                break;
        }
    }

    // true enquanto a pessoa ainda está soprando OU faz menos de RECONSTRUIR_TIMEOUT
    // segundos que parou — dá folga pra respirar sem cortar a sequência de palavras.
    bool DentroDoTimeoutReconstrucao()
    {
        return (Time.time - _ultimoSoproTime) < RECONSTRUIR_TIMEOUT;
    }

    // teia parada (estado de repouso): cada ponto treme no seu próprio ritmo,
    // e por cima disso uma onda lenta atravessa a tela inteira — dá a sensação
    // de a teia estar "respirando"/viva, em vez de só tremelicar no lugar.
    void AtualizarOscilacaoNormal()
    {
        for (int i = 0; i < COUNT; i++)
        {
            float ox = Mathf.Sin(Time.time * _freq[i] + _phase[i]) * TEIA_JITTER_AMPLITUDE;
            float oy = Mathf.Cos(Time.time * _freq[i] + _phase[i] + 1f) * TEIA_JITTER_AMPLITUDE;

            float onda = Mathf.Sin(Time.time * TEIA_ONDA_VELOCIDADE - _basePos[i].x * TEIA_ONDA_COMPRIMENTO) * TEIA_ONDA_AMPLITUDE;

            _pos[i] = _basePos[i] + new Vector2(ox, oy + onda);
        }
    }

    // =====================================================================
    // DISPERSÃO — sopro detectado: cada ponto ganha uma velocidade radial
    // (pra fora do centro) que vai acelerando, como se a teia explodisse
    // e saísse voando da tela.
    // =====================================================================
    void IniciarDispersao()
    {
        _estado = Estado.Dispersando;
        _proximoSpawnEm = 0f; // já libera a primeira palavra assim que a teia começar a sair
        if (_labelInstrucao != null) _labelInstrucao.gameObject.SetActive(false);

        for (int i = 0; i < COUNT; i++)
        {
            Vector2 baseDir = _pos[i].sqrMagnitude > 0.0001f ? _pos[i].normalized : Random.insideUnitCircle.normalized;
            float   desvio  = Random.Range(-25f, 25f);
            Vector2 dir     = Quaternion.Euler(0f, 0f, desvio) * baseDir;
            _vel[i] = dir * Random.Range(DISPERSAO_VEL_MIN, DISPERSAO_VEL_MAX);
        }
    }

    void AtualizarDispersao()
    {
        for (int i = 0; i < COUNT; i++)
        {
            Vector2 dir = _vel[i].normalized;
            _vel[i] += dir * DISPERSAO_ACEL * Time.deltaTime;
            _pos[i] += _vel[i] * Time.deltaTime;
        }
    }

    bool TodosPontosForaDaTela()
    {
        float limiteQuad = DISPERSAO_LIMITE * DISPERSAO_LIMITE;
        for (int i = 0; i < COUNT; i++)
            if (_pos[i].sqrMagnitude < limiteQuad) return false;
        return true;
    }

    // =====================================================================
    // RECONEXÃO — a teia volta de onde os pontos estão (espalhados pela
    // dispersão) até o layout final, cada ponto com um atraso aleatório e
    // chegando aos poucos. As arestas já são as do layout final desde o
    // início; como ficam compridas (e o alpha cai com a distância) elas só
    // "aparecem" quando os dois pontos conectados chegam perto um do outro
    // de novo — dá o efeito de teia se costurando/reconectando sozinha.
    // =====================================================================
    void IniciarReconexao()
    {
        LimparPalavras();

        for (int i = 0; i < COUNT; i++)
            _posOrigemReconexao[i] = _pos[i]; // de onde cada ponto está agora, espalhado

        GerarPontos();       // calcula o novo layout final (_basePos) — também já mexe em _pos, que sobrescrevemos abaixo
        ConstruirDelaunay(); // arestas do layout final, prontas desde já

        for (int i = 0; i < COUNT; i++)
        {
            _pos[i]              = _posOrigemReconexao[i]; // volta pro ponto de partida da animação
            _atrasoReconexao[i]  = Random.Range(0f, RECONEXAO_ATRASO_MAX);
        }

        _reconexaoTimer = 0f;
        _estado = Estado.Reconectando;
    }

    void AtualizarReconexao()
    {
        _reconexaoTimer += Time.deltaTime;

        bool todasChegaram = true;
        for (int i = 0; i < COUNT; i++)
        {
            float tLocal = Mathf.Clamp01((_reconexaoTimer - _atrasoReconexao[i]) / RECONEXAO_DURACAO_PONTO);
            if (tLocal < 1f) todasChegaram = false;

            float suave = 1f - (1f - tLocal) * (1f - tLocal); // ease-out: desacelera ao chegar no lugar
            _pos[i] = Vector2.Lerp(_posOrigemReconexao[i], _basePos[i], suave);
        }

        if (todasChegaram)
        {
            _estado = Estado.Normal;
            if (_labelInstrucao != null) _labelInstrucao.gameObject.SetActive(true);
        }
    }

    // =====================================================================
    // PALAVRAS — desde que a teia começa a se dispersar (ver Estado.Dispersando
    // no Update) até reconectar, aparece uma palavra por vez no centro,
    // encolhendo e sumindo como se voasse pra longe; a próxima já começa um
    // pouco antes da anterior terminar de sumir.
    // =====================================================================
    void AtualizarPalavras()
    {
        // força do sopro suavizada (0..1) — não pula pro valor novo na hora, vai
        // subindo/descendo aos poucos, então sopro sustentado acelera suavemente
        // e um sopro mais forte só se reflete no movimento aos poucos, sem tranco.
        float forcaAlvo = Mathf.InverseLerp(SOPRO_LIMIAR_PALAVRAS, SOPRO_LIMIAR_FORTE, _volumeSuavizado);
        _forcaSopro = Mathf.MoveTowards(_forcaSopro, forcaAlvo, FORCA_SOPRO_SUAVIZACAO * Time.deltaTime);

        // quantas palavras cabem na tela agora — sopro fraco/normal libera só
        // PALAVRA_MAX_SIMULTANEAS_BASE, sopro forte sustentado abre vagas até
        // PALAVRA_MAX_SIMULTANEAS_FORTE.
        int maxSimultaneas = Mathf.RoundToInt(Mathf.Lerp(PALAVRA_MAX_SIMULTANEAS_BASE, PALAVRA_MAX_SIMULTANEAS_FORTE, _forcaSopro));

        // só nasce palavra nova se: tem vaga livre, a pessoa está soprando
        // nesse instante, e já passou o cooldown mínimo.
        _proximoSpawnEm -= Time.deltaTime;
        if (_soprando && _palavrasAtivas.Count < maxSimultaneas && _proximoSpawnEm <= 0f)
        {
            SpawnPalavra(_forcaSopro);
            _proximoSpawnEm = Mathf.Lerp(PALAVRA_SPAWN_COOLDOWN_FRACO, PALAVRA_SPAWN_COOLDOWN_FORTE, _forcaSopro);
        }

        // aceleração extra enquanto sopra, em cima da aceleração natural de cada palavra
        // (usa a força já suavizada, não o volume cru — voo acelera suave, sem trancos)
        float acelSopro = _soprando ? _forcaSopro * PALAVRA_ACEL_SOPRO : 0f;

        for (int i = _palavrasAtivas.Count - 1; i >= 0; i--)
        {
            PalavraAtiva p = _palavrasAtivas[i];

            // a força "gruda": se em algum momento do voo a pessoa soprar mais
            // forte, a palavra passa a subir mais e afundar mais fundo daí em diante
            // (sempre suave, porque _forcaSopro já vem suavizada).
            p.forca = Mathf.Max(p.forca, _forcaSopro);

            // cada palavra acelera sozinha com o tempo (nunca trava, sempre termina de
            // sair), e soprar acelera ela mais rápido ainda, em cima disso.
            p.velocidade = Mathf.Min(p.velocidade + (PALAVRA_ACEL_BASE + acelSopro) * Time.deltaTime, PALAVRA_VEL_MAX);

            // ainda chegando: ritmo fixo (dá tempo de começar a ler); já no
            // meio do caminho: acompanha a velocidade acumulada da palavra
            float velocidadeEfetiva = (p.progresso < PALAVRA_CRESCE_FRACAO) ? PALAVRA_VEL_CHEGADA : p.velocidade;
            p.progresso += (velocidadeEfetiva / p.lentidaoLeitura) * Time.deltaTime;
            float t = Mathf.Clamp01(p.progresso);

            // a palavra nasce um pouco abaixo do centro e sobe pra tela enquanto
            // encolhe continuamente — é o encolher junto com o movimento que dá a
            // sensação de profundidade, como se fosse indo pro fundo da tela.
            // Quanto mais forte o sopro que ela pegou, mais ela sobe e mais fundo vai.
            float sobeDist = Mathf.Lerp(PALAVRA_SOBE_DIST_FRACO, PALAVRA_SOBE_DIST_FORTE, p.forca);
            p.rt.anchoredPosition = p.posInicial + p.direcao * (sobeDist * t);
            p.rt.localRotation    = Quaternion.Euler(0f, 0f, -90f + p.tombo * t);

            float escalaFim = Mathf.Lerp(PALAVRA_ESCALA_FIM_FRACO, PALAVRA_ESCALA_FIM_FORTE, p.forca);
            float escala;
            if (t < PALAVRA_CRESCE_FRACAO)
                escala = Mathf.Lerp(PALAVRA_ESCALA_CHEGADA, PALAVRA_ESCALA_INICIO, t / PALAVRA_CRESCE_FRACAO);
            else
                escala = Mathf.Lerp(PALAVRA_ESCALA_INICIO, escalaFim, (t - PALAVRA_CRESCE_FRACAO) / (1f - PALAVRA_CRESCE_FRACAO));
            p.rt.localScale = Vector3.one * escala;

            float alpha;
            if (t < PALAVRA_CRESCE_FRACAO)
                alpha = t / PALAVRA_CRESCE_FRACAO;
            else if (t > 1f - PALAVRA_FADE_FRACAO)
                alpha = 1f - (t - (1f - PALAVRA_FADE_FRACAO)) / PALAVRA_FADE_FRACAO;
            else
                alpha = 1f;
            Color cor = p.tmp.color;
            cor.a       = alpha;
            p.tmp.color = cor;

            if (t >= 1f)
            {
                Destroy(p.go);
                _palavrasAtivas.RemoveAt(i);
            }
        }
    }

    void SpawnPalavra(float forcaInicial)
    {
        if (_palavras.Count == 0 || _canvasTransform == null) return;

        GameObject go = new GameObject("Palavra");
        go.transform.SetParent(_canvasTransform, false);

        string palavra = ProximaPalavraAleatoria();

        TextMeshProUGUI tmp = go.AddComponent<TextMeshProUGUI>();
        tmp.text             = palavra;
        tmp.color            = new Color(0.227f, 0.180f, 0.122f, 0f);
        tmp.alignment        = TextAlignmentOptions.Center;
        tmp.enableAutoSizing = true; // a fonte se ajusta sozinha pra caber na caixa, palavra grande não nasce cortada
        tmp.fontSizeMin      = PALAVRA_FONTE_MIN;
        tmp.fontSizeMax      = PALAVRA_FONTE_MAX;

        // nasce um pouco abaixo do meio da tela, com um desvio lateral aleatório
        // (pra não empilhar quando várias nascem juntas) e também um desvio
        // aleatório pra cima/baixo (pra não nascerem sempre na mesma altura); o
        // desvio de ângulo (sorteado por palavra) é o que faz ela ir se
        // afastando pro seu lado conforme sobe, espalhando ainda mais o voo de cada uma.
        float   baixo       = PALAVRA_SPAWN_BAIXO + Random.Range(-PALAVRA_SPAWN_BAIXO_VARIACAO, PALAVRA_SPAWN_BAIXO_VARIACAO);
        Vector2 posInicial  = -EIXO_CIMA * baixo
                            + new Vector2(-EIXO_CIMA.y, EIXO_CIMA.x) * Random.Range(-PALAVRA_SPAWN_LADO, PALAVRA_SPAWN_LADO);
        float   angulo     = Random.Range(-ANGULO_MAX_VOO, ANGULO_MAX_VOO);
        Vector2 direcao    = Quaternion.Euler(0f, 0f, angulo) * EIXO_CIMA;

        RectTransform rt = go.GetComponent<RectTransform>();
        rt.anchorMin        = rt.anchorMax = new Vector2(0.5f, 0.5f);
        rt.pivot             = new Vector2(0.5f, 0.5f);
        rt.anchoredPosition  = posInicial;
        rt.sizeDelta         = new Vector2(PALAVRA_CAIXA_LARGURA, 220f); // caixa segura, cabe na tela física
        rt.localScale        = Vector3.one * PALAVRA_ESCALA_CHEGADA;
        rt.localRotation     = Quaternion.Euler(0f, 0f, -90f); // mesmo sentido da tela física

        float tombo = Random.Range(-PALAVRA_TOMBO_MAX, PALAVRA_TOMBO_MAX);

        // palavra com mais letras anda mais devagar, pra sobrar tempo de leitura
        float lentidao = Mathf.Max(1f, palavra.Length / 8f);

        _palavrasAtivas.Add(new PalavraAtiva
        {
            go = go, tmp = tmp, rt = rt, progresso = 0f, velocidade = PALAVRA_VEL_INICIAL, tombo = tombo,
            posInicial = posInicial, direcao = direcao, lentidaoLeitura = lentidao, forca = forcaInicial
        });
    }

    string ProximaPalavraAleatoria()
    {
        if (_filaIndice >= _filaEmbaralhada.Count)
        {
            _filaEmbaralhada = new List<string>(_palavras);
            for (int i = _filaEmbaralhada.Count - 1; i > 0; i--)
            {
                int j = Random.Range(0, i + 1);
                (_filaEmbaralhada[i], _filaEmbaralhada[j]) = (_filaEmbaralhada[j], _filaEmbaralhada[i]);
            }
            _filaIndice = 0;
        }
        return _filaEmbaralhada[_filaIndice++];
    }

    void LimparPalavras()
    {
        foreach (PalavraAtiva p in _palavrasAtivas) Destroy(p.go);
        _palavrasAtivas.Clear();
    }

    void OnPostRender()
    {
        _mat.SetPass(0);

        // a câmera é paisagem (16:9) mas a tela física é montada girada 90°,
        // então depois de rotacionar o conteúdo seus eixos local x/y trocam de
        // lugar — usamos uma escala ÚNICA (sem esticar) grande o bastante pra
        // cobrir a tela inteira, mesmo que recorte um pouco as bordas.
        float camH   = _cam.orthographicSize * 2f;
        float camW   = camH * _cam.aspect;
        float scale  = Mathf.Max(camH / CONTENT_W, camW / CONTENT_H) * TEIA_ESCALA;

        GL.PushMatrix();
        GL.MultMatrix(Matrix4x4.Rotate(Quaternion.Euler(0f, 0f, ROTATION_Z))
                     * Matrix4x4.Scale(new Vector3(scale, scale, 1f)));

        // a malha principal (a que forma PRO/PA/GAR) desenha como quads, não
        // GL.LINES — GL.LINES ignora espessura na maioria das plataformas e
        // sempre sai com 1px; assim dá pra controlar a espessura de verdade
        // (mesma técnica usada em teiabg.cs).
        GL.Begin(GL.QUADS);
        DrawDelaunayMesh();
        GL.End();

        GL.Begin(GL.LINES);
        if (_estado == Estado.Normal) DrawConexoesTexto();
        GL.End();

        GL.PopMatrix();
    }

    // linhas finas saindo de pontos externos da teia, esticando pra fora —
    // dá a sensação da teia "puxando"/se conectando com o texto de fundo por
    // trás. Só desenha com a teia formada e parada (estado normal).
    void DrawConexoesTexto()
    {
        for (int k = 0; k < CONEXAO_QTD; k++)
        {
            int idx = (k * 97) % COUNT; // espalha os índices escolhidos pela teia toda
            Vector2 p = _pos[idx];
            if (p.sqrMagnitude < 36f) continue; // só pontos já perto da borda externa

            Vector2 dirFora = p.normalized;
            Vector2 alvo    = p + dirFora * CONEXAO_ALCANCE;

            GL.Color(new Color(0.227f, 0.180f, 0.122f, CONEXAO_ALPHA));
            GL.Vertex3(p.x, p.y, 0);
            GL.Vertex3(alvo.x, alvo.y, 0);
        }
    }

    // teia fina de fundo: toda a triangulação de Delaunay, com alpha caindo
    // conforme a aresta é mais longa (linhas curtas mais escuras, longas quase
    // invisíveis) — é isso que dá o efeito de rendilhado cheio do print. Cada
    // linha também cintila levemente, fora de fase com as vizinhas (usa o
    // índice da aresta como deslocamento), dando um brilho vivo pra teia.
    void DrawDelaunayMesh()
    {
        float meiaEspessura = _teiaEspessura * 0.5f;

        for (int e = 0; e < _edgeA.Length; e++)
        {
            Vector2 pa = _pos[_edgeA[e]];
            Vector2 pb = _pos[_edgeB[e]];

            float dist  = Vector2.Distance(pa, pb);
            float alpha = Mathf.Clamp01(Mathf.Lerp(0.7f, 0f, dist / ALPHA_DIST));
            if (alpha <= 0.002f) continue;

            float brilho = 1f - TEIA_BRILHO_FORCA + TEIA_BRILHO_FORCA * Mathf.Sin(Time.time * TEIA_BRILHO_VELOCIDADE + e * 0.37f);
            alpha *= brilho;

            Vector2 dir  = dist > 0.0001f ? (pb - pa) / dist : Vector2.right;
            Vector2 perp = new Vector2(-dir.y, dir.x) * meiaEspessura;

            GL.Color(new Color(0.227f, 0.180f, 0.122f, alpha));
            GL.Vertex3(pa.x + perp.x, pa.y + perp.y, 0);
            GL.Vertex3(pb.x + perp.x, pb.y + perp.y, 0);
            GL.Vertex3(pb.x - perp.x, pb.y - perp.y, 0);
            GL.Vertex3(pa.x - perp.x, pa.y - perp.y, 0);
        }
    }

    void OnDestroy()
    {
        if (_mat != null) Destroy(_mat);
        if (_micDevice != null && Microphone.IsRecording(_micDevice)) Microphone.End(_micDevice);
    }

    // =====================================================================
    // Triangulação de Delaunay (Bowyer–Watson), auto-contida.
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