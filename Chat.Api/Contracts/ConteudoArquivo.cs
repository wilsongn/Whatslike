namespace Chat.Api.Contracts;

public sealed class ConteudoArquivo
{
    public Guid ArquivoId { get; set; }
    public string NomeArquivo { get; set; } = "";
    public long TamanhoBytes { get; set; }
    public string ContentType { get; set; } = "";
}
