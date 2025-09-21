using System.Text.Json;
using Chat.Shared.Net;
using Chat.Shared.Protocol;

namespace Chat.Server.Hub
{
    public class Router
    {
        private readonly SessionManager _sessions;
        private readonly GroupManager _groups;

        public Router(SessionManager sessions, GroupManager groups)
        {
            _sessions = sessions;
            _groups = groups;
        }

        public async Task HandleAsync(ClientSession sender, Envelope env)
        {
            switch (env.Type)
            {
                case MessageType.PrivateMsg:
                    await HandlePrivateAsync(sender, env);
                    break;
                case MessageType.GroupMsg:
                    await HandleGroupAsync(sender, env);
                    break;
                case MessageType.CreateGroup:
                    await HandleCreateGroupAsync(sender, env);
                    break;
                case MessageType.AddToGroup:
                    await HandleAddToGroupAsync(sender, env);
                    break;
                case MessageType.ListUsers:
                    await HandleListUsersAsync(sender);
                    break;
                case MessageType.FileChunk:
                    await HandleFileAsync(sender, env);
                    break;
                default:
                    await sender.SendAsync(ProtocolUtil.Make(
                        MessageType.Error, "server", sender.Username,
                        new ErrorMessage("UNSUPPORTED", $"Tipo {env.Type}")));
                    break;
            }
        }

        private async Task HandlePrivateAsync(ClientSession sender, Envelope env)
        {
            var pm = JsonMessageSerializer.Deserialize<PrivateMessage>(env.Payload);
            if (pm is null || string.IsNullOrWhiteSpace(pm.To))
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("BAD_REQUEST", "Destino inválido")));
                return;
            }

            var target = _sessions.Get(pm.To);
            if (target is null)
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("NOT_FOUND", "Usuário não conectado")));
                return;
            }

            var deliver = new Envelope(MessageType.PrivateMsg, sender.Username, pm.To, env.Payload);
            await target.SendAsync(deliver);
            await sender.SendAsync(ProtocolUtil.Make(
                MessageType.Ack, "server", sender.Username,
                new AckMessage("pm", "entregue")));
        }

        private async Task HandleGroupAsync(ClientSession sender, Envelope env)
        {
            var gm = JsonMessageSerializer.Deserialize<GroupMessage>(env.Payload);
            if (gm is null || string.IsNullOrWhiteSpace(gm.Group))
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("BAD_REQUEST", "Grupo inválido")));
                return;
            }

            foreach (var member in _groups.GetMembers(gm.Group))
            {
                if (member == sender.Username) continue;
                var target = _sessions.Get(member);
                if (target is not null)
                {
                    var deliver = new Envelope(MessageType.GroupMsg, sender.Username, gm.Group, env.Payload);
                    await target.SendAsync(deliver);
                }
            }

            await sender.SendAsync(ProtocolUtil.Make(
                MessageType.Ack, "server", sender.Username,
                new AckMessage("gmsg", "distribuído")));
        }

        private async Task HandleCreateGroupAsync(ClientSession sender, Envelope env)
        {
            var req = JsonMessageSerializer.Deserialize<CreateGroupRequest>(env.Payload);
            if (req is null || string.IsNullOrWhiteSpace(req.Name))
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("BAD_REQUEST", "Nome inválido")));
                return;
            }

            if (_groups.Create(req.Name))
            {
                _groups.AddUser(req.Name, sender.Username); // criador entra automaticamente
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Ack, "server", sender.Username,
                    new AckMessage("createGroup", "ok")));
            }
            else
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("CONFLICT", "Grupo já existe")));
            }
        }

        private async Task HandleAddToGroupAsync(ClientSession sender, Envelope env)
        {
            var req = JsonMessageSerializer.Deserialize<AddToGroupRequest>(env.Payload);
            if (req is null || string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.Username))
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("BAD_REQUEST", "Dados inválidos")));
                return;
            }

            if (_groups.AddUser(req.Name, req.Username))
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Ack, "server", sender.Username,
                    new AckMessage("addToGroup", "ok")));
            }
            else
            {
                await sender.SendAsync(ProtocolUtil.Make(
                    MessageType.Error, "server", sender.Username,
                    new ErrorMessage("NOT_FOUND", "Grupo não existe ou usuário já é membro")));
            }
        }

        private async Task HandleListUsersAsync(ClientSession sender)
        {
            var list = _sessions.ListUsers().ToArray();
            await sender.SendAsync(ProtocolUtil.Make(
                MessageType.Ack, "server", sender.Username,
                new ListUsersResponse(list)));
        }

        private async Task HandleFileAsync(ClientSession sender, Envelope env)
        {
            // Roteia frames de arquivo (header + chunks) como privado ou grupo
            var target = env.To;
            if (string.IsNullOrWhiteSpace(target))
            {
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, "server", sender.Username,
                    new ErrorMessage("BAD_REQUEST", "Destino do arquivo não informado")));
                return;
            }

            // 1) Grupo existente? (tem membros)
            var members = _groups.GetMembers(target).ToArray();
            if (members.Length > 0)
            {
                foreach (var m in members)
                {
                    if (m == sender.Username) continue;
                    var sess = _sessions.Get(m);
                    if (sess is not null)
                    {
                        // Encaminha o mesmo envelope
                        await sess.SendAsync(new Envelope(MessageType.FileChunk, sender.Username, target, env.Payload));
                    }
                }
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Ack, "server", sender.Username,
                    new AckMessage("file/group", $"enviado para {members.Length - 1} membro(s)")));
                return;
            }

            // 2) Usuário conectado?
            var dest = _sessions.Get(target);
            if (dest is not null)
            {
                await dest.SendAsync(new Envelope(MessageType.FileChunk, sender.Username, target, env.Payload));
                await sender.SendAsync(ProtocolUtil.Make(MessageType.Ack, "server", sender.Username,
                    new AckMessage("file/pm", "entregue")));
                return;
            }

            // 3) Ninguém encontrado
            await sender.SendAsync(ProtocolUtil.Make(MessageType.Error, "server", sender.Username,
                new ErrorMessage("NOT_FOUND", "Destino não encontrado (usuário/grupo)")));
        }

    }
}
