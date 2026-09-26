using System.Runtime.CompilerServices;

// Adapter DLL（ArcherReposition.MCM）需要访问本程序集的 internal 类型（Config/SettingsManager）
[assembly: InternalsVisibleTo("ArcherReposition.MCM")]
