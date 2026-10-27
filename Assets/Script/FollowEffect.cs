using System;
using UnityEngine;

public class FollowEffect : MonoBehaviour
{
    // public void OnEnable()
    // {
    //     Registry<FollowEffect>.Add(this);
    // }
    //
    // public void OnDisable()
    // {
    //     Registry<FollowEffect>.Remove(this);
    // }

    public void OnUpdate_Position_World(Vector3 position) => gameObject.transform.position = position;
    public void OnUpdate_Position_Local(Vector3 position) => gameObject.transform.localPosition = position;
    public void OnUpdate_TransformUp(Vector3 dir) => gameObject.transform.up = dir;

    public void Update()
    {
        OnUpdate_Position_World (GameManager.Instance.mFollowTargetPosition);
    }
}