using System;

namespace Chat.Shared.Protocol
{
    public enum MessageType
    {
        Auth = 1,
        PrivateMsg = 2,
        GroupMsg = 3,
        FileChunk = 4,
        Ack = 5,
        Error = 6,
        ListUsers = 7,
        ListGroups = 8,
        CreateGroup = 9,
        AddToGroup = 10,
        Ping = 11,
        Pong = 12
    }
}
