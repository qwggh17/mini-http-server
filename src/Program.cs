using System.Net;
using System.Net.Sockets;
using System.Text;

class Program
{
    //функция main
    static void Main()
    {
        int port = 8080;

        TcpListener server = new TcpListener(IPAddress.Loopback, port);
        server.Start();

        Console.WriteLine($"Server started on http://localhost:{port}/");

        while (true)
        {
            //этап подключения подключение

            TcpClient client = server.AcceptTcpClient();
            NetworkStream stream = client.GetStream();

            //чтение HTTP-запроса
            byte[] buffer = new byte[4096];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);

            if (bytesRead == buffer.Length)
            {
                SendSimpleResponse(
                    stream,
                    "413 Payload Too Large",
                    "text/html; charset=utf-8",
                    "<h1>413 Payload Too Large</h1><p>Request is too large.</p>",
                    true
                );

                client.Close();
                continue;
            }

            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            string[] requestLines = request.Split("\r\n");
            string requestLine = requestLines[0];

            string[] requestParts = requestLine.Split(' ');

            if (requestParts.Length < 3)
            {
                SendSimpleResponse(
                    stream,
                    "400 Bad Request",
                    "text/html; charset=utf-8",
                    "<h1>400 Bad Request</h1>",
                    true
                );

                client.Close();
                continue;
            }

            string method = requestParts[0];
            string path = requestParts[1];
            string version = requestParts[2];

            //парсинг HTTP-заголовков
            Dictionary<string, string> headers = ParseHeaders(requestLines);

            string host = headers.GetValueOrDefault("Host", "-");
            string userAgent = headers.GetValueOrDefault("User-Agent", "-");
            string accept = headers.GetValueOrDefault("Accept", "-");

            //пути к папкам
            string pagesPath = Path.Combine(AppContext.BaseDirectory, "pages");
            string logsPath = Path.Combine(AppContext.BaseDirectory, "logs");

            //подготовка ответа
            string status;
            string contentType;
            string responseBody;

            if (ContainsPathTraversal(path))
            {
                status = "403 Forbidden";
                contentType = "text/html; charset=utf-8";
                responseBody = "<h1>403 Forbidden</h1><p>Path traversal attempt blocked.</p>";
            }
            else if (method == "OPTIONS")
            {
                status = "204 No Content";
                contentType = "text/plain; charset=utf-8";
                responseBody = "";
            }
            else if (method != "GET" && method != "HEAD")
            {
                status = "405 Method Not Allowed";
                contentType = "text/html; charset=utf-8";
                responseBody = "<h1>405 Method Not Allowed</h1>";
            }
            else if (path == "/")
            {
                status = "200 OK";
                contentType = "text/html; charset=utf-8";
                responseBody = File.ReadAllText(Path.Combine(pagesPath, "index.html"));
            }
            else if (path == "/about")
            {
                status = "200 OK";
                contentType = "text/html; charset=utf-8";
                responseBody = File.ReadAllText(Path.Combine(pagesPath, "about.html"));
            }
            else
            {
                string relativePath = path.TrimStart('/');

                string filePath = Path.Combine(pagesPath, relativePath);

                if (File.Exists(filePath))
                {
                    status = "200 OK";
                    contentType = GetContentType(filePath);
                    responseBody = File.ReadAllText(filePath);
                }
                else
                {
                    status = "404 Not Found";
                    contentType = "text/html; charset=utf-8";
                    responseBody = File.ReadAllText(Path.Combine(pagesPath, "404.html"));
                }
            }

            //вывод информации в консоль
            Console.WriteLine("\n==============================");
            Console.WriteLine($"Request: {method} {path}");
            Console.WriteLine($"Status: {status}");
            Console.WriteLine("==============================");

            //запись запроса в лог
            LogRequest(
                logsPath,
                method,
                path,
                version,
                status,
                host,
                userAgent,
                accept
            );

            //отправка HTTP-ответа
            bool includeBody = method != "HEAD" && method != "OPTIONS";

            SendSimpleResponse(
                stream,
                status,
                contentType,
                responseBody,
                includeBody
            );

            client.Close();
        }
    }

    //парсинг HTTP-заголовков
    static Dictionary<string, string> ParseHeaders(string[] requestLines)
    {
        Dictionary<string, string> headers = new Dictionary<string, string>();

        for (int i = 1; i < requestLines.Length; i++)
        {
            string line = requestLines[i];

            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            int colonIndex = line.IndexOf(':');

            if (colonIndex <= 0)
            {
                continue;
            }

            string headerName = line.Substring(0, colonIndex).Trim();
            string headerValue = line.Substring(colonIndex + 1).Trim();

            headers[headerName] = headerValue;
        }

        return headers;
    }
    //логгирование запросов
    static void LogRequest(
        string logsPath,
        string method,
        string path,
        string version,
        string status,
        string host,
        string userAgent,
        string accept
    )
    {
        Directory.CreateDirectory(logsPath);

        string time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");

        string accessLogLine =
            $"[{time}] {method} {path} {version} -> {status} | Host: {host} | User-Agent: {userAgent} | Accept: {accept}";

        string accessLogPath = Path.Combine(logsPath, "access.log");
        File.AppendAllText(accessLogPath, accessLogLine + Environment.NewLine);

        if (!status.StartsWith("2"))
        {
            string errorLogLine =
                $"[{time}] ERROR {status} | {method} {path} | Host: {host} | User-Agent: {userAgent}";

            string errorLogPath = Path.Combine(logsPath, "error.log");
            File.AppendAllText(errorLogPath, errorLogLine + Environment.NewLine);
        }
    }

    //защита путей (path traversal)
    static bool ContainsPathTraversal(string path)
    {
        string decodedPath = Uri.UnescapeDataString(path);

        return decodedPath.Contains("..") ||
               decodedPath.Contains("\\") ||
               path.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("%5c", StringComparison.OrdinalIgnoreCase);
    }

    //обьявление MIME-типа
    static string GetContentType(string filePath)
    {
        string extension = Path.GetExtension(filePath).ToLower();

        return extension switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".ico" => "image/x-icon",
            ".txt" => "text/plain; charset=utf-8",
            _ => "application/octet-stream"
        };
    }

    //отправка HTTP-response
    static void SendSimpleResponse(
        NetworkStream stream,
        string status,
        string contentType,
        string responseBody,
        bool includeBody
    )
    {
        string response =
            $"HTTP/1.1 {status}\r\n" +
            "Server: MiniCSharpHttpServer\r\n" +
            $"Date: {DateTime.UtcNow:R}\r\n" +
            $"Last-Modified: {DateTime.UtcNow:R}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {Encoding.UTF8.GetByteCount(responseBody)}\r\n" +
            "X-Content-Type-Options: nosniff\r\n" +
            "X-Frame-Options: DENY\r\n" +
            "Referrer-Policy: no-referrer\r\n" +
            "Cache-Control: no-store\r\n" +
            "Allow: GET, HEAD, OPTIONS\r\n" +
            "\r\n";

        if (includeBody)
        {
            response += responseBody;
        }

        byte[] responseBytes = Encoding.UTF8.GetBytes(response);

        stream.Write(responseBytes, 0, responseBytes.Length);
    }
}
