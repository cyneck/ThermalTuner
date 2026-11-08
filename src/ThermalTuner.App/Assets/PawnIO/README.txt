PawnIO 模块说明 / 许可
========================

本目录下的 IntelMSR.bin 来自 PawnIO.Modules 0.2.11
（https://github.com/namazso/PawnIO.Modules），用于通过 PawnIO 内核驱动
执行 MSR 读/写。

许可：GNU Lesser General Public License v2.1（见同目录 COPYING.LESSER.txt）

对应源码：https://github.com/namazso/PawnIO.Modules
          模块源文件：IntelMSR.p

为什么需要这个模块
------------------
PawnIO 内核驱动本身只执行"已签名白名单模块"，不直接暴露任意 MSR 访问。
本产品所需的寄存器能力全部由 IntelMSR 模块的写白名单覆盖：

  MSR_PKG_POWER_LIMIT      0x610   PL1 / PL2 / Tau
  MSR_OC_MAILBOX           0x150   电压偏移（OC Mailbox）
  MSR_VR_CURRENT_CONFIG
  MSR_MISC_FEATURE_CONTROL

注意：LibreHardwareMonitor 0.9.6 内嵌的同名模块是**只读**版本
（仅含 ioctl_read_msr），不能替换本文件。

替换 / 升级
-----------
可把更新版本的 IntelMSR.bin 放到：
  %LOCALAPPDATA%\ThermalTuner\driver\IntelMSR.bin
该路径优先于内置资源，便于不重新打包即可升级模块。
