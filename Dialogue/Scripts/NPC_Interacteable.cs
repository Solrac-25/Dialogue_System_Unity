using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

public class NPC_Interactable : MonoBehaviour
{
    [Header("Diálogo")]
    [Tooltip("ID de la primera línea. Da igual escribir '1', '01' o '0001': son el mismo ID.")]
    public string dialogueStartId = "0001";

    [Header("Detección")]
    public float interactionRange = 3f;

    // Se configura en el Input Action Asset (ej. Player/Interact)
    [Header("Input System")]
    [SerializeField] private InputActionReference interactAction;

    // Todos los NPC activos: si hay varios al alcance, solo responde el más cercano
    private static readonly List<NPC_Interactable> activeNpcs = new List<NPC_Interactable>();

    private DialogueSystem dialogueSystem;
    private Transform player;
    private bool playerInRange = false;

    // Reintento de búsqueda de referencias (por si el Player o el DialogueSystem aparecen más tarde)
    private float nextLookupTime;
    private bool warnedMissingDialogue;

    // Con "Enter Play Mode Options" las estáticas sobreviven entre partidas
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatics()
    {
        activeNpcs.Clear();
    }

    void OnEnable()
    {
        activeNpcs.Add(this);

        if (interactAction != null)
        {
            interactAction.action.performed += OnInteractPerformed;
            // Habilitamos la acción nosotros: antes dependía de que otro script la activara
            interactAction.action.Enable();
        }
    }

    void OnDisable()
    {
        activeNpcs.Remove(this);

        // IMPORTANTE: desuscribirse para evitar callbacks sobre un objeto destruido
        if (interactAction != null)
            interactAction.action.performed -= OnInteractPerformed;
    }

    void Start()
    {
        RefreshReferences();
    }

    void Update()
    {
        if (dialogueSystem == null || player == null)
        {
            playerInRange = false;

            if (Time.unscaledTime >= nextLookupTime)
                RefreshReferences();

            return;
        }

        float distance = Vector3.Distance(transform.position, player.position);
        playerInRange = distance <= interactionRange;
    }

    void RefreshReferences()
    {
        nextLookupTime = Time.unscaledTime + 1f;

        if (dialogueSystem == null)
            dialogueSystem = FindAnyObjectByType<DialogueSystem>();

        if (player == null)
        {
            GameObject playerObject = GameObject.FindGameObjectWithTag("Player");
            if (playerObject != null) player = playerObject.transform;
        }

        if (dialogueSystem == null && !warnedMissingDialogue)
        {
            warnedMissingDialogue = true;
            Debug.LogError($"[{name}] No se encontró DialogueSystem en la escena");
        }
    }

    private void OnInteractPerformed(InputAction.CallbackContext context)
    {
        if (!playerInRange) return;
        if (!IsClosestInRange()) return;

        Interact();
    }

    // Con varios NPC cerca, solo el más cercano al jugador inicia su diálogo
    private bool IsClosestInRange()
    {
        if (player == null) return false;

        float myDistance = (transform.position - player.position).sqrMagnitude;

        foreach (NPC_Interactable other in activeNpcs)
        {
            if (other == this || !other.playerInRange) continue;

            float otherDistance = (other.transform.position - player.position).sqrMagnitude;
            if (otherDistance < myDistance) return false;
        }

        return true;
    }

    public void Interact()
    {
        if (dialogueSystem == null)
        {
            Debug.LogError($"[{name}] No se puede iniciar el diálogo: falta DialogueSystem");
            return;
        }

        // No reiniciar un diálogo en curso, ni reabrirlo en el mismo frame en que se cerró
        // (pasa si la misma tecla sirve para Interact y para Continue)
        if (!dialogueSystem.CanStartDialogue) return;

        dialogueSystem.StartDialogue(dialogueStartId);
    }

    void OnDrawGizmosSelected()
    {
        Gizmos.color = playerInRange ? Color.green : Color.yellow;
        Gizmos.DrawWireSphere(transform.position, interactionRange);
    }
}
