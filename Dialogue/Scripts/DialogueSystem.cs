using UnityEngine;
using UnityEngine.UI;
using TMPro;
using UnityEngine.InputSystem;
using System.Collections;
using System.Collections.Generic;
using System.IO;

// ============================================
// CLASE DE DATOS
// ============================================
[System.Serializable]
public class DialogueLine
{
    public string id;              
    public string characterName;   
    public string text;            
    public string nextId;          
    public string expression;      
    public string side;            
    public string color;           
    public string config;          
}

// ============================================
// CONFIGURACIÓN DE UI (una por panel)
// Solo "Dialogue Text" es obligatorio. Todo lo demás es opcional:
// un texto de narrador puede no tener panel, nombre ni imágenes.
// ============================================
[System.Serializable]
public class DialogueUIConfig
{
    [Header("OBLIGATORIO")]
    [Tooltip("Único campo obligatorio: el texto donde se escribe el diálogo.")]
    public TextMeshProUGUI dialogueText;

    [Header("Opcionales (déjalos vacíos si no los necesitas)")]
    [Tooltip("Panel que se muestra/oculta con el diálogo. Si está vacío, se muestra/oculta solo el texto.")]
    public GameObject dialoguePanel;
    [Tooltip("Texto del nombre del personaje. Si está vacío, no se muestra el nombre.")]
    public TextMeshProUGUI nameText;
    [Tooltip("Botón de continuar. Si está vacío, se avanza solo con las Input Actions.")]
    public Button continueButton;

    [Header("Imágenes de Personajes (opcionales)")]
    public Image leftCharacterImage;
    public Image rightCharacterImage;

    [Header("Modo de avance")]
    [Tooltip("Si es true, el diálogo avanza automáticamente tras typingSpeed + autoAdvanceDelay segundos")]
    public bool autoAdvance = false;
    [Tooltip("Segundos de espera DESPUÉS de terminar de escribir el texto, antes de pasar a la siguiente línea")]
    public float autoAdvanceDelay = 3f;

    // Color original del texto en el Inspector (para líneas sin color en el CSV)
    [System.NonSerialized] public Color defaultTextColor = Color.white;
    [System.NonSerialized] public bool warnedFallback;

    /// <summary>Una config es utilizable si tiene al menos el texto asignado.</summary>
    public bool IsUsable => dialogueText != null;

    /// <summary>Lo que se muestra/oculta: el panel si existe, y si no, el propio texto.</summary>
    public GameObject Root => dialoguePanel != null
        ? dialoguePanel
        : (dialogueText != null ? dialogueText.gameObject : null);
}

// ============================================
// COMPONENTE PRINCIPAL
// ============================================
public class DialogueSystem : MonoBehaviour
{
    [Header("Configuración 1")]
    public DialogueUIConfig config1;

    [Header("Configuración 2")]
    public DialogueUIConfig config2;

    [Header("Configuración General")]
    public float typingSpeed = 0.03f;
    public string csvFileName = "Dialogue_ES.csv";

    [Header("Rutas Relativas (se pueden cambiar desde el Inspector)")]
    [Tooltip("Carpeta (relativa a Assets) donde están los CSV. Ej: 'Dialogue/Idiomas' o 'assets/Script_Dialogue/Dialogue/Idiomas'.\n" +
             "Es una PISTA: si el CSV no está ahí, se busca solo por todo Assets. " +
             "Click derecho en el título del componente > 'Detectar carpeta del CSV' rellena este campo automáticamente.")]
    public string csvRelativeFolder = "Dialogue/Idiomas";
    [Tooltip("Ruta relativa a cualquier carpeta Resources donde están los personajes. Ej: 'Characters'")]
    public string charactersResourcesPath = "Characters";

    [Header("Input System")]
    [SerializeField] private InputActionReference continueAction;
    [SerializeField] private InputActionReference skipAction;

    // Diccionario: ID -> línea de diálogo
    private Dictionary<string, DialogueLine> allDialogues = new Dictionary<string, DialogueLine>();
    private DialogueLine currentLine;
    private bool isTyping = false;
    private bool waitingForClick = false;

    private Dictionary<string, Sprite> imageCache = new Dictionary<string, Sprite>();

    // Referencia a la configuración activa actualmente
    private DialogueUIConfig activeConfig;

    // Config cuyo botón "continuar" tiene ahora mismo nuestro listener (para poder quitárselo)
    private DialogueUIConfig subscribedConfig;

    // Referencia a la corrutina de auto-avance para poder cancelarla
    private Coroutine autoAdvanceCoroutine;

    // Frames en los que empezó / terminó el último diálogo. Sirven para que una
    // misma pulsación (ej. "E" como Interact y como Continue) no inicie y avance
    // el diálogo a la vez, ni lo cierre y lo reabra en el mismo frame.
    private int lastStartFrame = -1;
    private int lastEndFrame = -1;

    // Ruta del CSV ya resuelta (se recalcula si cambia el nombre o el archivo desaparece)
    private string resolvedCsvPath;
    private string resolvedCsvFileName;

    /// <summary>True mientras haya un diálogo en pantalla.</summary>
    public bool IsDialogueActive =>
        activeConfig != null && activeConfig.Root != null && activeConfig.Root.activeSelf;

    /// <summary>
    /// True si se puede iniciar un diálogo ahora mismo (no hay uno en curso y
    /// no acaba de cerrarse en este mismo frame). Lo consulta NPC_Interactable.
    /// </summary>
    public bool CanStartDialogue => !IsDialogueActive && Time.frameCount != lastEndFrame;

    // Colores predefinidos
    private Dictionary<string, Color> textColors = new Dictionary<string, Color>()
    {
        { "blue", new Color(0.2f, 0.6f, 1f) },
        { "red", new Color(1f, 0.3f, 0.3f) },
        { "green", new Color(0.3f, 1f, 0.3f) },
        { "yellow", new Color(1f, 0.9f, 0.2f) },
        { "white", Color.white },
        { "black", Color.black },
        { "purple", new Color(0.8f, 0.2f, 1f) },
        { "orange", new Color(1f, 0.6f, 0.1f) }
    };

    void Start()
    {
        CaptureDefaultColor(config1);
        CaptureDefaultColor(config2);

        // Ocultar todo al inicio
        HideConfig(config1);
        HideConfig(config2);

        bool anyUsable = (config1 != null && config1.IsUsable) || (config2 != null && config2.IsUsable);
        if (!anyUsable)
        {
            Debug.LogError("DialogueSystem: no hay ningún 'Dialogue Text' asignado. " +
                           "Arrastra un TextMeshProUGUI al campo 'Dialogue Text' de la Configuración 1 (o 2).", this);
        }

        LoadDialogueFromCSV();
    }

    void OnEnable()
    {
        if (continueAction != null)
        {
            continueAction.action.performed += OnContinuePerformed;
            continueAction.action.Enable();
        }

        if (skipAction != null)
        {
            skipAction.action.performed += OnSkipPerformed;
            skipAction.action.Enable();
        }
    }

    void OnDisable()
    {
        if (continueAction != null)
            continueAction.action.performed -= OnContinuePerformed;

        if (skipAction != null)
            skipAction.action.performed -= OnSkipPerformed;
    }

    void OnDestroy()
    {
        UnsubscribeButton();
    }

    static void CaptureDefaultColor(DialogueUIConfig config)
    {
        if (config != null && config.dialogueText != null)
            config.defaultTextColor = config.dialogueText.color;
    }

    // Oculta panel/texto e imágenes de una config (todo es opcional salvo el texto)
    static void HideConfig(DialogueUIConfig config)
    {
        if (config == null) return;

        GameObject root = config.Root;
        if (root != null) root.SetActive(false);

        SetImageActive(config.leftCharacterImage, false);
        SetImageActive(config.rightCharacterImage, false);
    }

    static void SetImageActive(Image image, bool active)
    {
        if (image != null) image.gameObject.SetActive(active);
    }

    // Suscribe el botón de una configuración (si lo tiene)
    void SubscribeButton(DialogueUIConfig config)
    {
        if (config != null && config.continueButton != null)
        {
            config.continueButton.onClick.AddListener(OnContinueClicked);
            subscribedConfig = config;
        }
    }

    // Quita el listener del botón al que se lo pusimos
    void UnsubscribeButton()
    {
        if (subscribedConfig != null && subscribedConfig.continueButton != null)
            subscribedConfig.continueButton.onClick.RemoveListener(OnContinueClicked);

        subscribedConfig = null;
    }

    // ¿Se debe ignorar el input manual ahora mismo?
    bool ManualInputBlocked()
    {
        if (!IsDialogueActive) return true;
        if (activeConfig.autoAdvance) return true;                // modo automático: ignora input
        if (Time.frameCount == lastStartFrame) return true;       // la misma pulsación que abrió el diálogo
        return false;
    }

    void OnContinuePerformed(InputAction.CallbackContext context)
    {
        if (ManualInputBlocked()) return;
        OnContinueClicked();
    }

    void OnSkipPerformed(InputAction.CallbackContext context)
    {
        if (ManualInputBlocked()) return;

        if (isTyping)
            CompleteTyping();
    }

    void CompleteTyping()
    {
        StopAllCoroutines();
        activeConfig.dialogueText.text = currentLine.text;
        isTyping = false;
        waitingForClick = true;
    }

    // ============================================
    // CARGAR CSV - LA CARPETA PUEDE ESTAR EN CUALQUIER SITIO DE Assets
    // ============================================

    /// <summary>
    /// Recarga el CSV. Opcionalmente cambia antes el archivo (ej. "Dialogue_ENG.csv").
    /// </summary>
    public void ReloadDialogue(string newCsvFileName = null)
    {
        if (!string.IsNullOrWhiteSpace(newCsvFileName))
            csvFileName = newCsvFileName;

        LoadDialogueFromCSV();
    }

    void LoadDialogueFromCSV()
    {
        string path = FindCsvPath();

        if (path == null)
        {
            Debug.LogError($"No se encontró '{csvFileName}' ni en '{csvRelativeFolder}' ni en ninguna otra carpeta de Assets. " +
                           "Comprueba que el archivo existe y que el nombre (csvFileName) es correcto.");
            return;
        }

        Debug.Log($"Cargando CSV desde: {path}");

        // Limpiar al recargar (cambio de idioma) para no mezclar líneas de dos idiomas
        allDialogues.Clear();

        string[] lines = File.ReadAllLines(path);

        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;

            string[] parts = ParseCSVLine(lines[i]);

            // Mínimo 4 columnas (ID, Nombre, Texto, NextId). El resto son opcionales, así que
            // una línea de narrador puede ser simplemente:  5,,Texto del narrador,6
            if (parts.Length < 4)
            {
                Debug.LogWarning($"Línea {i} incompleta (mínimo 4 columnas: ID, Nombre, Texto, NextId; encontradas {parts.Length}), se omite");
                continue;
            }

            DialogueLine line = new DialogueLine();
            line.id = NormalizeId(parts[0]);
            line.characterName = parts[1].Trim();
            line.text = parts[2].Trim();
            line.nextId = NormalizeId(parts[3]);
            line.expression = Column(parts, 4).ToLower();
            line.side = Column(parts, 5).ToLower();
            line.color = Column(parts, 6).ToLower();

            // Columna config (8ª): si no existe o está vacía, se usa la configuración "1"
            string cfg = Column(parts, 7);
            line.config = cfg.Length == 0 ? "1" : cfg;

            allDialogues[line.id] = line;
        }

        Debug.Log($"Cargadas {allDialogues.Count} líneas de diálogo");
    }

    // Columna opcional: devuelve "" si la línea no la tiene
    static string Column(string[] parts, int index)
    {
        return index < parts.Length ? parts[index].Trim() : "";
    }

    // Unifica los IDs: "0001", "01" y "1" son el mismo ID ("1"); "000A" -> "A"; "-1" se queda igual.
    // Así da igual si Excel/LibreOffice quitó los ceros del CSV o si el NPC escribe "0001".
    static string NormalizeId(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "-1";

        id = id.Trim().ToUpper();
        if (id == "-1") return id;

        id = id.TrimStart('0');
        return id.Length == 0 ? "0" : id;
    }

    // Devuelve la ruta absoluta del CSV o null.
    // 1) Pista del Inspector (csvRelativeFolder)  2) Búsqueda automática por todo Assets
    string FindCsvPath()
    {
        if (string.IsNullOrWhiteSpace(csvFileName)) return null;

        // Ya resuelta antes y sigue existiendo
        if (resolvedCsvFileName == csvFileName && resolvedCsvPath != null && File.Exists(resolvedCsvPath))
            return resolvedCsvPath;

        string path = null;

        // 1) Pista del Inspector
        string folder = SanitizeFolder(csvRelativeFolder);
        string hinted = (Path.Combine(Application.dataPath, folder, csvFileName)).Replace("\\", "/");
        if (File.Exists(hinted))
        {
            path = hinted;
        }
        else
        {
            // 2) Búsqueda en todo Assets
            path = SearchInAssets(csvFileName, folder);

            if (path != null)
            {
                Debug.Log($"'{csvFileName}' no estaba en '{csvRelativeFolder}'. Encontrado en '{ToRelativeFolder(path)}'. " +
                          "Para saltarte la búsqueda: click derecho en el componente > 'Detectar carpeta del CSV'.");
            }
        }

        resolvedCsvFileName = csvFileName;
        resolvedCsvPath = path;
        return path;
    }

    // Acepta "Dialogue/Idiomas", "/Dialogue/Idiomas/", "Assets/Dialogue/Idiomas", con \ o /
    static string SanitizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return "";

        folder = folder.Trim().Replace("\\", "/").Trim('/');

        // Distingue mayúsculas a propósito: la raíz de Unity es "Assets", pero una subcarpeta
        // propia llamada "assets" (minúscula) NO se debe recortar.
        if (folder == "Assets") return "";
        if (folder.StartsWith("Assets/", System.StringComparison.Ordinal)) folder = folder.Substring(7);

        return folder;
    }

    static string SearchInAssets(string fileName, string hintFolder)
    {
        string[] found;
        try
        {
            found = Directory.GetFiles(Application.dataPath, fileName, SearchOption.AllDirectories);
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Error buscando '{fileName}' en Assets: {e.Message}");
            return null;
        }

        string root = Application.dataPath.Replace("\\", "/");
        List<string> matches = new List<string>();

        foreach (string f in found)
        {
            if (Path.GetFileName(f) != fileName) continue;

            string normalized = f.Replace("\\", "/");
            if (IsIgnoredByUnity(normalized.Substring(root.Length))) continue;

            matches.Add(normalized);
        }

        if (matches.Count == 0) return null;

        matches.Sort(System.StringComparer.Ordinal);
        string best = matches[0];

        // Si hay varios, preferir el que esté en una carpeta con el mismo nombre que la pista (ej. "Idiomas")
        if (matches.Count > 1)
        {
            string hintName = Path.GetFileName(hintFolder);
            foreach (string m in matches)
            {
                if (!string.IsNullOrEmpty(hintName) && Path.GetFileName(Path.GetDirectoryName(m)) == hintName)
                {
                    best = m;
                    break;
                }
            }

            Debug.LogWarning($"Hay {matches.Count} archivos llamados '{fileName}' en Assets. Se usa: {best}");
        }

        return best;
    }

    // Unity no importa carpetas que empiezan por "." ni terminan en "~"
    static bool IsIgnoredByUnity(string pathInsideAssets)
    {
        foreach (string segment in pathInsideAssets.Split('/'))
        {
            if (segment.StartsWith(".") || segment.EndsWith("~")) return true;
        }
        return false;
    }

    static string ToRelativeFolder(string fullPath)
    {
        string root = Application.dataPath.Replace("\\", "/");
        string folder = Path.GetDirectoryName(fullPath).Replace("\\", "/");
        return folder.StartsWith(root) ? folder.Substring(root.Length).TrimStart('/') : folder;
    }

    // Rellena csvRelativeFolder con la carpeta real del CSV (para que tus compañeros no tengan que escribirla)
    [ContextMenu("Detectar carpeta del CSV")]
    void AutoDetectCsvFolder()
    {
        resolvedCsvPath = null; // forzar nueva búsqueda
        string path = FindCsvPath();

        if (path == null)
        {
            Debug.LogError($"No se encontró '{csvFileName}' en Assets. Revisa el nombre en csvFileName.");
            return;
        }

        csvRelativeFolder = ToRelativeFolder(path);
#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
        Debug.Log($"csvRelativeFolder = '{csvRelativeFolder}'");
    }

    string[] ParseCSVLine(string line)
    {
        List<string> result = new List<string>();
        bool inQuotes = false;
        string current = "";

        foreach (char c in line)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (c == ',' && !inQuotes)
            {
                result.Add(current.Trim());
                current = "";
            }
            else
                current += c;
        }

        result.Add(current.Trim());
        return result.ToArray();
    }

    // ============================================
    // INICIAR DIÁLOGO
    // ============================================
    public void StartDialogue(string startId)
    {
        startId = NormalizeId(startId);

        if (!allDialogues.ContainsKey(startId))
        {
            Debug.LogError($"ID de diálogo no encontrado: {startId} (cargadas {allDialogues.Count} líneas de '{csvFileName}')");
            return;
        }

        lastStartFrame = Time.frameCount;
        ShowLine(startId);
    }

    public void StartDialogue(int startId)
    {
        string hexId = startId.ToString("X4");
        StartDialogue(hexId);
    }

    // ============================================
    // MOSTRAR LÍNEA
    // ============================================
    void ShowLine(string id)
    {
        id = NormalizeId(id);

        if (id != "-1" && !allDialogues.ContainsKey(id))
            Debug.LogWarning($"El NextId '{id}' no existe en el CSV. Se termina el diálogo.");

        if (id == "-1" || !allDialogues.ContainsKey(id))
        {
            EndDialogue();
            return;
        }

        currentLine = allDialogues[id];

        // Seleccionar configuración según el campo config del CSV
        if (!SelectActiveConfig(currentLine.config))
        {
            EndDialogue();
            return;
        }

        // 1. Nombre (opcional)
        if (activeConfig.nameText != null)
            activeConfig.nameText.text = currentLine.characterName;

        // 2. Color del texto
        ApplyTextColor();

        // 3. Imagen del personaje (opcional)
        UpdateCharacterImage();

        // 4. Escribir texto
        StopAllCoroutines();
        StartCoroutine(TypeText(currentLine.text));
    }

    bool SelectActiveConfig(string configValue)
    {
        // Cancelar auto-avance anterior si existe
        if (autoAdvanceCoroutine != null)
        {
            StopCoroutine(autoAdvanceCoroutine);
            autoAdvanceCoroutine = null;
        }

        // Quitar el listener del botón de la config anterior antes de cambiar
        UnsubscribeButton();

        // Ocultar ambas configs primero
        HideConfig(config1);
        HideConfig(config2);

        // La config pedida por el CSV, y la otra como alternativa
        bool wantsSecond = configValue == "2";
        DialogueUIConfig wanted = wantsSecond ? config2 : config1;
        DialogueUIConfig alternative = wantsSecond ? config1 : config2;

        if (wanted != null && wanted.IsUsable)
        {
            activeConfig = wanted;
        }
        else if (alternative != null && alternative.IsUsable)
        {
            activeConfig = alternative;

            if (!alternative.warnedFallback)
            {
                alternative.warnedFallback = true;
                Debug.LogWarning($"La configuración '{configValue}' no tiene 'Dialogue Text' asignado. " +
                                 "Se usa la otra configuración.");
            }
        }
        else
        {
            activeConfig = null;
            Debug.LogError("No hay ninguna configuración con 'Dialogue Text' asignado en el DialogueSystem.", this);
            return false;
        }

        SubscribeButton(activeConfig);

        // Mostrar el panel (o solo el texto si no hay panel)
        activeConfig.Root.SetActive(true);

        // Imágenes ocultas hasta que la línea decida cuál mostrar
        SetImageActive(activeConfig.leftCharacterImage, false);
        SetImageActive(activeConfig.rightCharacterImage, false);

        return true;
    }

    void ApplyTextColor()
    {
        if (activeConfig == null) return;

        string colorName = currentLine.color;

        if (string.IsNullOrEmpty(colorName))
        {
            // Sin color en el CSV: el que tenga el texto en el Inspector
            activeConfig.dialogueText.color = activeConfig.defaultTextColor;
        }
        else if (textColors.ContainsKey(colorName))
        {
            activeConfig.dialogueText.color = textColors[colorName];
        }
        else if (ColorUtility.TryParseHtmlString(colorName, out Color parsedColor))
        {
            activeConfig.dialogueText.color = parsedColor;
        }
        else
        {
            activeConfig.dialogueText.color = Color.white;
            Debug.LogWarning($"Color '{colorName}' no reconocido. Usando blanco.");
        }
    }

    void UpdateCharacterImage()
    {
        if (activeConfig == null) return;

        Image leftImage = activeConfig.leftCharacterImage;
        Image rightImage = activeConfig.rightCharacterImage;

        SetImageActive(leftImage, false);
        SetImageActive(rightImage, false);

        // Narrador / texto sin personaje: sin nombre o sin expresión no hay retrato
        if (string.IsNullOrWhiteSpace(currentLine.characterName) ||
            string.IsNullOrWhiteSpace(currentLine.expression))
            return;

        Image targetImage = (currentLine.side == "left") ? leftImage : rightImage;

        // Esa configuración no tiene imagen para ese lado: no pasa nada, simplemente no se muestra
        if (targetImage == null) return;

        string imageKey = currentLine.characterName + "/" + currentLine.expression;

        Sprite sprite;

        if (imageCache.ContainsKey(imageKey))
        {
            sprite = imageCache[imageKey];
        }
        else
        {
            // RUTA RELATIVA A RESOURCES: configurable desde el Inspector
            string path = $"{charactersResourcesPath}/{currentLine.characterName}/{currentLine.expression}";
            sprite = Resources.Load<Sprite>(path);

            if (sprite == null)
            {
                Debug.LogError($"No se encontró la imagen en Resources: {path}. Asegúrate de que exista en una carpeta Resources.");
                return;
            }

            imageCache[imageKey] = sprite;
        }

        targetImage.sprite = sprite;
        targetImage.preserveAspect = true;
        targetImage.gameObject.SetActive(true);
    }

    IEnumerator TypeText(string fullText)
    {
        isTyping = true;
        waitingForClick = false;
        activeConfig.dialogueText.text = "";

        foreach (char letter in fullText)
        {
            activeConfig.dialogueText.text += letter;
            yield return new WaitForSeconds(typingSpeed);
        }

        isTyping = false;
        waitingForClick = true;

        // Si la config activa tiene autoAdvance, iniciar corrutina de espera
        if (activeConfig.autoAdvance)
        {
            autoAdvanceCoroutine = StartCoroutine(AutoAdvanceAfterDelay());
        }
    }

    IEnumerator AutoAdvanceAfterDelay()
    {
        // Esperar el tiempo configurado en autoAdvanceDelay
        yield return new WaitForSeconds(activeConfig.autoAdvanceDelay);

        // Pasar a la siguiente línea
        ShowLine(currentLine.nextId);
    }

    void OnContinueClicked()
    {
        if (activeConfig == null) return;

        // Si es autoAdvance, ignorar clicks (aunque el botón esté suscrito por si acaso)
        if (activeConfig.autoAdvance) return;

        if (isTyping)
        {
            CompleteTyping();
        }
        else if (waitingForClick)
        {
            ShowLine(currentLine.nextId);
        }
    }

    // ============================================
    // FINALIZAR DIÁLOGO
    // ============================================
    void EndDialogue()
    {
        // Cancelar auto-avance si está activo
        if (autoAdvanceCoroutine != null)
        {
            StopCoroutine(autoAdvanceCoroutine);
            autoAdvanceCoroutine = null;
        }

        // Quitar el listener del botón antes de cerrar
        UnsubscribeButton();

        if (activeConfig != null)
        {
            if (activeConfig.nameText != null) activeConfig.nameText.text = "";
            HideConfig(activeConfig);
        }

        currentLine = null;
        activeConfig = null;
        isTyping = false;
        waitingForClick = false;
        lastEndFrame = Time.frameCount;

        // OJO: ya NO se deshabilitan continueAction/skipAction aquí. Si compartían
        // acción con Interact (misma tecla), el NPC dejaba de poder iniciar diálogos.

        Debug.Log("Diálogo finalizado");
    }
}
