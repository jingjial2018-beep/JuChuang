# Third-party notices

The WeChat 4.x SQLCipher page format and read-only `Config.Cipher` discovery in
`Services/WeChatProfileService.cs` were implemented with reference to the
`fanyuantaier/wechatauto-replica` project:

https://github.com/fanyuantaier/wechatauto-replica

That project is licensed under the Apache License 2.0:

https://github.com/fanyuantaier/wechatauto-replica/blob/main/LICENSE

聚窗仅使用上述研究来读取当前登录账号在 `contact.db` 中的本人资料行，不读取
消息、会话或朋友圈数据库，不保存数据库密钥或解密后的数据库。
