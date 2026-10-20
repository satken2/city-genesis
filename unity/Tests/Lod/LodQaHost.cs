using System.Collections;
using UnityEngine;

public sealed class LodQaHost : MonoBehaviour
{
    public Coroutine Run(IEnumerator work) => StartCoroutine(work);
}
