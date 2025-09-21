namespace Chat.Shared.Protocol
{
    // Autenticação
    public record AuthRequest(string Username, string? Password);

    // Mensagem privada (usuário → usuário)
    public record PrivateMessage(string To, string Text);

    // Mensagem de grupo
    public record GroupMessage(string Group, string Text);

    // Gestão de grupos
    public record CreateGroupRequest(string Name);
    public record AddToGroupRequest(string Name, string Username);

    // Envio de arquivos (headers e chunks — implementação no cliente/servidor)
    public record FileChunkHeader(string Id, string Target, string FileName, long TotalBytes, int ChunkSize);
    public record FileChunk(string Id, int Index, int Count, byte[] Data);

    // Listagem de usuários conectados
    public record ListUsersRequest();
    public record ListUsersResponse(string[] Users);

    // Infra
    public record AckMessage(string CorrelationId, string? Note);
    public record ErrorMessage(string Code, string Message);
}
