using Microsoft.AspNetCore.Http;
using Newtonsoft.Json;
using OfiGest.Context;
using OfiGest.Utilities;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;

namespace OfiGest.Manegers
{
    public class CorreoManager
    {
        private readonly ApplicationDbContext _context;
        private string _cachedToken;
        private DateTime _tokenExpiration;
        public CorreoManager(ApplicationDbContext context)
        {
            _context = context;
        }

        //private bool EnviarCorreo(string destinatario, string asunto, string cuerpoHtml)
        //{
        //    var usuarioDb = _context.Usuarios.FirstOrDefault(u => u.Correo == destinatario);
        //    if (usuarioDb == null) return false;

        //    try
        //    {
        //        var remitente = Environment.GetEnvironmentVariable("Correo_Remitente");
        //        var smtpUsuario = Environment.GetEnvironmentVariable("Correo_Usuario");
        //        var clave = Environment.GetEnvironmentVariable("Correo_clave");
        //        var servidor = Environment.GetEnvironmentVariable("Correo_servidor");
        //        var puerto = int.Parse(Environment.GetEnvironmentVariable("Correo_puerto"));

        //        var mensaje = new MailMessage
        //        {
        //            From = new MailAddress(remitente),
        //            Subject = asunto,
        //            Body = cuerpoHtml,
        //            IsBodyHtml = true
        //        };

        //        mensaje.To.Add(destinatario);

        //        using var smtp = new SmtpClient(servidor, puerto)
        //        {
        //            Credentials = new NetworkCredential(smtpUsuario, clave),
        //            EnableSsl = true
        //        };

        //        smtp.Send(mensaje);
        //        return true;
        //    }
        //    catch
        //    {
        //        return false;
        //    }
        //}



       
        private string GetAccessTokenAsync()
        {
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.UtcNow < _tokenExpiration)
                return _cachedToken;

            var clientId = Environment.GetEnvironmentVariable("ClientId");
            var tenantId = Environment.GetEnvironmentVariable("TenantId");
            var clientSecret = Environment.GetEnvironmentVariable("ClientSecret");

            var url = $"https://login.microsoftonline.com/{tenantId}/oauth2/v2.0/token";

            var body = new Dictionary<string, string>
    {
        { "client_id", clientId },
        { "scope", "https://graph.microsoft.com/.default" },
        { "client_secret", clientSecret },
        { "grant_type", "client_credentials" }
    };

            using var httpClient = new HttpClient();
            var response = httpClient
                    .PostAsync(url, new FormUrlEncodedContent(body))
                    .GetAwaiter()
                    .GetResult();

            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
                throw new Exception("Error al obtener token: " + json);

            dynamic result = JsonConvert.DeserializeObject(json);

            _cachedToken = result.access_token;
            _tokenExpiration = DateTime.UtcNow.AddSeconds((int)result.expires_in - 60);

            return _cachedToken;
        }

        private bool EnviarCorreoAsync(string destinatario, string asunto, string cuerpoHtml)
        {
            var usuarioDb = _context.Usuarios.FirstOrDefault(u => u.Correo == destinatario);
            if (usuarioDb == null) return false;

            var senderEmail = Environment.GetEnvironmentVariable("SenderEmail");
            var token = GetAccessTokenAsync();

            using var httpClient = new HttpClient();
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            try
            {
                var mail = new
                {
                    message = new
                    {
                        subject = asunto,
                        body = new
                        {
                            contentType = "HTML",
                            content = cuerpoHtml
                        },
                        toRecipients = new[]
                        {
                    new
                    {
                        emailAddress = new
                        {
                            address = destinatario
   }
                    }
                }
                    },
                    saveToSentItems = "false"
                };

                var json = JsonConvert.SerializeObject(mail);
                var content = new StringContent(json, Encoding.UTF8, "application/json");

                var response = httpClient.PostAsync(
                    $"https://graph.microsoft.com/v1.0/users/{senderEmail}/sendMail",
                    content
                )
                .GetAwaiter()
                .GetResult();

                return true;
            }
            catch
            {
                return false;
            }
        }
        public bool UsuarioExiste(string correo)
        {
            return _context.Usuarios.Any(u => u.Correo == correo);
        }

        public bool ValidarTokenRestablecimiento(string correo, string token)
        {
            if (string.IsNullOrWhiteSpace(correo) || string.IsNullOrWhiteSpace(token))
                return false;

            var correoNormalizado = correo.Trim().ToLower();
            var tokenRecibido = token.Trim();

            var usuario = _context.Usuarios.FirstOrDefault(u =>
                u.Correo.ToLower() == correoNormalizado &&
                u.Token != null &&
                u.TokenExpira != null &&
                u.RequiereRestablecer == true);

            if (usuario == null) return false;

            return string.Equals(usuario.Token?.Trim(), tokenRecibido, StringComparison.Ordinal) &&
                   usuario.TokenExpira >= DateTime.UtcNow;
        }

        private string ReemplazarVariables(string plantilla, Dictionary<string, string> variables)
        {
            foreach (var kvp in variables)
                plantilla = plantilla.Replace($"{{{{{kvp.Key}}}}}", kvp.Value);

            return plantilla;
        }

        public bool EnviarRestablecimientoClave(string correo)
        {
            var usuario = _context.Usuarios.FirstOrDefault(u => u.Correo == correo);
            if (usuario == null) return false;

            var baseUrl = Environment.GetEnvironmentVariable("Correo_BaseUrl");
            if (string.IsNullOrWhiteSpace(baseUrl))
                return false;

            // Verificar si ya existe un token vigente
            var tokenVigente = usuario.RequiereRestablecer == true &&
                             usuario.TokenExpira != null &&
                             usuario.TokenExpira >= DateTime.UtcNow;

            if (tokenVigente)
            {
             
                return false;
            }

            var token = TokenGenerate.GenerarToken();
            var minutosExpiracion = int.Parse(Environment.GetEnvironmentVariable("CorreoRestablecer_ExpiracionMinutos") ?? "30");

            usuario.Token = token;
            usuario.TokenExpira = DateTime.UtcNow.AddMinutes(minutosExpiracion);
            usuario.RequiereRestablecer = true;
            _context.SaveChanges();

            var tokenCodificado = WebUtility.UrlEncode(token);
            var enlace = $"{baseUrl}/Cuenta/Restablecer?correo={WebUtility.UrlEncode(correo)}&token={tokenCodificado}";

            var rutaPlantilla = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "templates", "Restablecer.html");
            if (!File.Exists(rutaPlantilla)) return false;

            var plantillaHtml = File.ReadAllText(rutaPlantilla);
            var nombreCompleto = $"{usuario.Nombre} {usuario.Apellido}".Trim();

            var cuerpoHtml = ReemplazarVariables(plantillaHtml, new Dictionary<string, string>
            {
                ["NombreCompleto"] = nombreCompleto,
                ["Acción"] = "el restablecimiento de tu contraseña",
                ["TextoBotón"] = "Restablecer contraseña",
                ["Enlace"] = enlace,
                ["MinutosExpiración"] = minutosExpiracion.ToString(),
                ["AñoActual"] = DateTime.Now.Year.ToString()
            });

            return EnviarCorreoAsync(correo, "Restablecimiento de contraseña - OfiGest", cuerpoHtml);
        }

        public bool EnviarActivacionCuenta(string correo)
        {
            var usuario = _context.Usuarios.FirstOrDefault(u => u.Correo == correo);
            if (usuario == null) return false;

            var baseUrl = Environment.GetEnvironmentVariable("Correo_BaseUrl");
            if (string.IsNullOrWhiteSpace(baseUrl))
                return false;

            // Verificar si ya existe un token vigente
            var tokenVigente = usuario.RequiereRestablecer == true &&
                             usuario.TokenExpira != null &&
                             usuario.TokenExpira >= DateTime.UtcNow;

            if (tokenVigente)
            {
               
                return false;
            }

            var token = TokenGenerate.GenerarToken();
            var minutosExpiracion = int.Parse(Environment.GetEnvironmentVariable("CorreoActivacion_ExpiracionMinutos") ?? "60");

            usuario.Token = token;
            usuario.TokenExpira = DateTime.UtcNow.AddMinutes(minutosExpiracion);
            usuario.RequiereRestablecer = true;
            _context.SaveChanges();

            var tokenCodificado = WebUtility.UrlEncode(token);
            var enlace = $"{baseUrl}/Cuenta/Restablecer?correo={WebUtility.UrlEncode(correo)}&token={tokenCodificado}";

            var rutaPlantilla = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "templates", "Activacion.html");
            if (!File.Exists(rutaPlantilla)) return false;

            var plantillaHtml = File.ReadAllText(rutaPlantilla);
            var nombreCompleto = $"{usuario.Nombre} {usuario.Apellido}".Trim();

            var cuerpoHtml = ReemplazarVariables(plantillaHtml, new Dictionary<string, string>
            {
                ["NombreCompleto"] = nombreCompleto,
                ["Acción"] = "la activación de tu cuenta",
                ["TextoBotón"] = "Definir contraseña",
                ["Enlace"] = enlace,
                ["MinutosExpiración"] = minutosExpiracion.ToString(),
                ["AñoActual"] = DateTime.Now.Year.ToString()
            });

            return EnviarCorreoAsync(correo, "Activación de cuenta - OfiGest", cuerpoHtml);
        }
    }
}