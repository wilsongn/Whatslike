using System;

namespace Chat.Shared.Protocol
{
    public enum MessageType
    {
        Auth,
        Ack,
        Error,
        ListUsers,
        PrivateMsg,
        GroupMsg,
        CreateGroup,
        AddToGroup,
        FileChunk,

        // NOVOS
        Ping,
        Pong,

        // Interno (entre nós): envelope roteado via bus
        Routed // não sai para o cliente WPF
    }
}
