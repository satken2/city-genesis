using System;
using UnityEngine;

/// <summary>
/// Show the UNVERIFIED caption only while the core runs without Steam authentication (debug runs count as verified).
/// Tauri sends system:verification when Unity starts and whenever the status changes; stay hidden until then.
/// Keep this object active in the scene: Awake must run to subscribe, after which the label hides itself.
/// </summary>
public class UnverifiedLabel : MonoBehaviour
{
    private static bool unverified;
    private static event Action Changed;

    public static void SetVerified(bool verified)
    {
        unverified = !verified;
        Changed?.Invoke();
    }

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    private static void ResetStatic()
    {
        unverified = false;
        Changed = null;
    }

    private void Awake()
    {
        Changed += Apply;
        Apply();
    }

    private void OnDestroy()
    {
        Changed -= Apply;
    }

    private void Apply()
    {
        gameObject.SetActive(unverified);
    }
}
