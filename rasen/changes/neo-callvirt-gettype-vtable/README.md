# neo-callvirt-gettype-vtable

wave2 C4: Neo callvirt this-null + cannot resolve VTable slot for GetType() (~20 失败)。IL 类型调用继承自 System.Object 的 GetType() 等,VTable 无 slot。re-audit + 修 + 全量验证
