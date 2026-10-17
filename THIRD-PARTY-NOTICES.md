# Third-party notices

The WeChat 4.x SQLCipher page format and read-only `Config.Cipher` discovery in
`Services/WeChatProfileService.cs` were implemented with reference to the
`fanyuantaier/wechatauto-replica` project:

https://github.com/fanyuantaier/wechatauto-replica

That project is licensed under the Apache License 2.0:

https://github.com/fanyuantaier/wechatauto-replica/blob/main/LICENSE

聚窗仅使用上述研究来读取当前登录账号在 `contact.db` 中的本人资料行，不读取
消息、会话或朋友圈数据库；数据库密钥不写入磁盘，解密副本仅在读取期间临时存在于
受限目录中，并在读取完成后删除。
