# 测试数据

`tone.flac` 来自同作者的 NCM-Better-Download/tests/fixtures：一秒长的 440/660 Hz 原创正弦音，不含商业音乐，沿用原项目 GPL-3.0 许可。

测试会在 build/test-runs 内为副本写入虚构标签，再生成临时加密数据，验证卡片得到歌曲标签且不改变转换结果。不使用用户密钥或歌曲作为测试夹具。
