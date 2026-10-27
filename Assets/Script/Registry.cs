using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Collections.Generic;

// 这是一个泛型静态类，T 是想管理的类型
public static class Registry<T> where T : class
{
    // 这里的 Items 就是那个“花名册”，存了所有活着的 T
    // 使用 HashSet 保证唯一性，查找和删除速度极快
    public static readonly HashSet<T> Items = new HashSet<T>();

    // 入册：当对象生成时调用
    public static void Add(T item) => Items.Add(item);

    // 除名：当对象销毁时调用
    public static void Remove(T item) => Items.Remove(item);

    // 实用功能：获取数量
    public static int Count => Items.Count;


    // // 告诉注册表：我出生了，把我记下来
    // Registry<Enemy>.Add(this); 
    //
    // // 告诉注册表：我死了/被禁用了，把我划掉
    // Registry<Enemy>.Remove(this);
    // // 注意：遍历时如果要删除元素，必须先转为 List，否则会报错
    // var allEnemies = new List<Enemy>(Registry<Enemy>.Items);
    //
    // foreach (var enemy in allEnemies)
    // {
    //     enemy.Die(); // 调用敌人的死亡方法
    // }
}