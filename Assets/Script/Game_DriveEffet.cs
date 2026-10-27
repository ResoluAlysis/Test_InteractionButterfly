using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Random = UnityEngine.Random;

//游戏中需要实现驱赶效果的对象需挂载该脚本以注册驱赶效果
public class Game_DriveEffet : MonoBehaviour
{
    private Rigidbody2D _RB2D;
    public void Awake()
    {
        _RB2D = GetComponent<Rigidbody2D>();
    }

    public void OnEnable() => Registry<Game_DriveEffet>.Add(this);
    public void OnDisable() => Registry<Game_DriveEffet>.Remove(this);

    [SerializeField]private DriveState mCurState = DriveState.Idle;
    [SerializeField]private DriveState mLastState = DriveState.Nove;

    public Vector2 mDriveSourcePosition;
    private enum DriveState
    {
        Nove,
        Idle, //待机
        Move, //移动
        Accelerate, //被驱赶加速 
        SlowDown, //巡航结束后减速
        Cruise //被驱赶后巡航
    }

    public float _driveTime = 0;
    private float _maxAccelerateTime = 0;
    private float _accelerateTime = 0;
    private float _maxSlowDownTime = 0;
    private float _slowDownTime = 0;
    
    private Vector3 dirOffset = Vector3.zero;

    public void DoDrive(float time)
    {
        _driveTime = Mathf.Min(_driveTime + time, 3f);
        mCurState = DriveState.Accelerate;
    }
    
    public void FixedUpdate()
    {
        //当当前状态与上个状态不同时
        if (mLastState != mCurState)
        {
            //Debug.Log($"切换状态：{mCurState}（上一状态：{mLastState}）");
            mLastState = mCurState;
            //存储一次上个状态的速度
            mLastStateSpeed = mCurSpeed;
            
            _maxAccelerateTime = Random.Range(0.2f, 0.5f);
            _accelerateTime = _maxAccelerateTime;
            _maxSlowDownTime = Random.Range(0.2f, 0.5f);
            _slowDownTime = _maxSlowDownTime;
            
            dirOffset = new Vector3(Random.Range(-1f, 1f),Random.Range(-1f, 1f),0);
        }
        
        switch (mCurState)
        {
            case DriveState.Nove:
                break;
            case DriveState.Idle:
                mCurSpeed = 0.1f;
                break;
            case DriveState.Move:
                mCurSpeed = mMoveSpeed;
                break;
            case DriveState.Accelerate:
                mCurSpeed = Mathf.Lerp(mDriveSpeed, mLastStateSpeed, _accelerateTime/_maxAccelerateTime);
                _accelerateTime -= Time.fixedDeltaTime;
                if(_accelerateTime <= 0) mCurState = DriveState.Cruise;
                break;
            case DriveState.SlowDown:
                mCurSpeed = Mathf.Lerp(mMoveSpeed, mLastStateSpeed, _slowDownTime/_maxSlowDownTime);
                _slowDownTime -= Time.fixedDeltaTime;
                if (_slowDownTime <= 0) mCurState = Random.value < 0.5f ? DriveState.Move : DriveState.Idle;
                break;
            case DriveState.Cruise:
                _driveTime -= Time.fixedDeltaTime;
                if (_driveTime <= 0)
                {
                    _driveTime = 0;
                    mCurState = DriveState.SlowDown;
                    
                    mMoveSpeed = Random.Range(0.2f, 2f);
                    mDriveSpeed =  Random.Range(5f, 8f);
                }
                break;
            default:
                throw new ArgumentOutOfRangeException();
        }

        OnUpdateSpeed();
    }

    [SerializeField] private float mMoveSpeed = 1f;
    [SerializeField] private float mDriveSpeed = 5f;
    [SerializeField] private float mCurSpeed;
    [SerializeField] private float mLastStateSpeed;
    
    public void OnUpdateSpeed()
    {
        transform.up += dirOffset * 0.02f;
        
        _RB2D.velocity = mCurSpeed * transform.up;
    }
}
