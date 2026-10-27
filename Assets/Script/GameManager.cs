using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

public class GameManager : MonoBehaviour
{
    public static GameManager Instance;

    void Awake() => Instance = Instance == null ? this : gameObject.AddComponent<GameManager>();

    public void Start()
    {
        _camera = Camera.main;
        
        Debug.Log("[GameManager]: 初始化...");
        
        StartCoroutine(Game_UpdateDriveEffet());
        StartCoroutine(Game_ScreenLoopEffet());

        for (int i = 0; i < gameDriveObjNum; i++) Instantiate(mDriveObj, Get_ScreenPosition(), Quaternion.identity);
    }

    public GameObject mScreenLU, mScreenRD;
    
    public GameObject mDriveObj;

    public Vector2 Get_ScreenPosition() => new (Random.Range(mScreenLU.transform.position.x, mScreenRD.transform.position.x), Random.Range(mScreenLU.transform.position.y, mScreenRD.transform.position.y));
    public void Update()
    {
        mFollowTargetPosition = _camera.ScreenToWorldPoint(Input.mousePosition);

        //Game_FollowEffect(Input.mousePosition);
    }

    public void Game_FollowEffect(Vector3 target) => mFollowTargetPosition = target;
    public Vector2 mFollowTargetPosition;
    private Camera _camera;

    public IEnumerator Game_UpdateDriveEffet()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.5f);
            
            var allDriveObj = new List<Game_DriveEffet>(Registry<Game_DriveEffet>.Items);
            var allDriveSourceObj = new List<Game_DriveSourceEffect>(Registry<Game_DriveSourceEffect>.Items);
            foreach (var driveSourceObj in allDriveSourceObj)
            {
                foreach (var driveObj in allDriveObj)
                {
                    var distance = Vector2.Distance(driveSourceObj.transform.position, driveObj.transform.position);
                    
                    if (!(distance <= gameDriveDistance)) continue;
                    Debug.Log($"距离达标：{distance}");
                    driveObj.DoDrive(1f);
                    driveObj.mDriveSourcePosition =  driveSourceObj.transform.position;
                }
            }
        }
    }

    public IEnumerator Game_ScreenLoopEffet()
    {
        while (true)
        {
            yield return new WaitForSeconds(0.5f);
            
            var allScreenLoopObj = new List<Game_ScreenLoopEffect>(Registry<Game_ScreenLoopEffect>.Items);
            foreach (var screenLoopObj in allScreenLoopObj)
            {
                if (screenLoopObj.transform.position.x < mScreenLU.transform.position.x - 1f) screenLoopObj.transform.position = new Vector2(mScreenRD.transform.position.x, screenLoopObj.transform.position.y);
                if (screenLoopObj.transform.position.y < mScreenRD.transform.position.y - 1f) screenLoopObj.transform.position = new Vector2(screenLoopObj.transform.position.x, mScreenLU.transform.position.y);
                if (screenLoopObj.transform.position.y > mScreenLU.transform.position.y + 1f) screenLoopObj.transform.position = new Vector2(screenLoopObj.transform.position.x, mScreenRD.transform.position.y);
                if (screenLoopObj.transform.position.x > mScreenRD.transform.position.x + 1f) screenLoopObj.transform.position = new Vector2(mScreenLU.transform.position.x, screenLoopObj.transform.position.y);
            }
        }
    }

    [Header("触发驱赶的半径")]
    public float gameDriveDistance = 1f;
    [Header("初始生成多少个被驱逐物体")]
    public int gameDriveObjNum = 16;
}
