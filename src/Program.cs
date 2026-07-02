using System.Net;
using System.Net.Sockets;
using System.Text;

class Program
{
    const int RequestBufferSize = 4096;
    const int MaxBodySize = 8192;

    // константы и функция main
    static void Main()
    {
        int port = 8080;

        // запуск TCP-сервера
        TcpListener server = new TcpListener(IPAddress.Loopback, port);
        server.Start();

        Console.WriteLine($"Server started on http://localhost:{port}/");

        while (true)
        {
            // ожидание подключения клиента
            TcpClient client = server.AcceptTcpClient();
            NetworkStream stream = client.GetStream();

            // чтение HTTP-запроса
            byte[] buffer = new byte[RequestBufferSize];
            int bytesRead = stream.Read(buffer, 0, buffer.Length);

            // пустой запрос
            if (bytesRead == 0)
            {
                client.Close();
                continue;
            }

            // защита от слишком большого запроса
            if (bytesRead == buffer.Length)
            {
                SendSimpleResponse(
                    stream,
                    "413 Payload Too Large",
                    "text/html; charset=utf-8",
                    "<h1>413 Payload Too Large</h1><p>Request headers are too large.</p>",
                    true
                );

                client.Close();
                continue;
            }

            // преобразование байтов в текст запроса
            string request = Encoding.UTF8.GetString(buffer, 0, bytesRead);

            // разделение заголовков и тела запроса
            string[] requestPartsRaw = request.Split("\r\n\r\n", 2);
            string headerPart = requestPartsRaw[0];
            string body = requestPartsRaw.Length > 1 ? requestPartsRaw[1] : "";

            // разбор первой строки HTTP-запроса
            string[] requestLines = headerPart.Split("\r\n");
            string requestLine = requestLines[0];

            string[] requestParts = requestLine.Split(' ');

            // защита от битого request line
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

            // метод, путь и версия HTTP
            string method = requestParts[0];
            string path = requestParts[1];
            string version = requestParts[2];

            // парсинг HTTP-заголовков
            Dictionary<string, string> headers = ParseHeaders(requestLines);

            string host = headers.GetValueOrDefault("Host", "-");
            string userAgent = headers.GetValueOrDefault("User-Agent", "-");
            string accept = headers.GetValueOrDefault("Accept", "-");

            // пути к папкам проекта
            string pagesPath = Path.Combine(AppContext.BaseDirectory, "pages");
            string logsPath = Path.Combine(AppContext.BaseDirectory, "logs");

            // подготовка HTTP-ответа
            string status;
            string contentType;
            string responseBody;

            // обработка API-запросов
            if (TryHandleApiRequest(
                method,
                path,
                headers,
                body,
                out status,
                out contentType,
                out responseBody
            ))
            {
            }

            // обработка POST-запросов вне API
            else if (TryHandlePostRequest(
                method,
                path,
                headers,
                out status,
                out contentType,
                out responseBody
            ))
            {
            }

            // защита от path traversal
            else if (ContainsPathTraversal(path))
            {
                status = "403 Forbidden";
                contentType = "text/html; charset=utf-8";
                responseBody = "<h1>403 Forbidden</h1><p>Path traversal attempt blocked.</p>";
            }

            // обработка OPTIONS
            else if (method == "OPTIONS")
            {
                status = "204 No Content";
                contentType = "text/plain; charset=utf-8";
                responseBody = "";
            }

            // запрет неподдерживаемых методов
            else if (method != "GET" && method != "HEAD")
            {
                status = "405 Method Not Allowed";
                contentType = "text/html; charset=utf-8";
                responseBody = "<h1>405 Method Not Allowed</h1>";
            }

            // обработка статических файлов
            else
            {
                HandleStaticRequest(
                    path,
                    pagesPath,
                    out status,
                    out contentType,
                    out responseBody
                );
            }

            // вывод информации в консоль
            Console.WriteLine("\n==============================");
            Console.WriteLine($"Request: {method} {path}");
            Console.WriteLine($"Status: {status}");
            Console.WriteLine("==============================");

            // запись запроса в логи
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

            // HEAD и OPTIONS не получают тело ответа
            bool includeBody = method != "HEAD" && method != "OPTIONS";

            // отправка HTTP-ответа
            SendSimpleResponse(
                stream,
                status,
                contentType,
                responseBody,
                includeBody
            );

            // закрытие подключения
            client.Close();
        }
    }

    // обработка API
    static bool TryHandleApiRequest(
        string method,
        string path,
        Dictionary<string, string> headers,
        string body,
        out string status,
        out string contentType,
        out string responseBody
    )
    {
        status = "";
        contentType = "";
        responseBody = "";

        // не API-запрос
        if (!path.StartsWith("/api/"))
        {
            return false;
        }

        // проверка работоспособности сервера
        if (path == "/api/health" && method == "GET")
        {
            status = "200 OK";
            contentType = "application/json; charset=utf-8";
            responseBody = "{\"status\":\"ok\",\"server\":\"MiniCSharpHttpServer\"}";
            return true;
        }

        // тестовый POST endpoint
        if (path == "/api/echo" && method == "POST")
        {
            // проверка Content-Length
            if (!headers.ContainsKey("Content-Length"))
            {
                status = "400 Bad Request";
                contentType = "application/json; charset=utf-8";
                responseBody = "{\"error\":\"Missing Content-Length\"}";
                return true;
            }

            // проверка корректности Content-Length
            if (!int.TryParse(headers["Content-Length"], out int contentLength))
            {
                status = "400 Bad Request";
                contentType = "application/json; charset=utf-8";
                responseBody = "{\"error\":\"Invalid Content-Length\"}";
                return true;
            }

            // ограничение размера тела запроса
            if (contentLength > MaxBodySize)
            {
                status = "413 Payload Too Large";
                contentType = "application/json; charset=utf-8";
                responseBody = "{\"error\":\"Request body is too large\"}";
                return true;
            }

            // возврат тела запроса клиенту
            status = "200 OK";
            contentType = "application/json; charset=utf-8";
            responseBody = "{\"received\":\"" + body + "\"}";
            return true;
        }

        // неизвестный API-маршрут
        status = "404 Not Found";
        contentType = "application/json; charset=utf-8";
        responseBody = "{\"error\":\"API endpoint not found\"}";
        return true;
    }

    // обработка POST вне API
    static bool TryHandlePostRequest(
        string method,
        string path,
        Dictionary<string, string> headers,
        out string status,
        out string contentType,
        out string responseBody
    )
    {
        status = "";
        contentType = "";
        responseBody = "";

        // не POST-запрос
        if (method != "POST")
        {
            return false;
        }

        // проверка Content-Length
        if (!headers.ContainsKey("Content-Length"))
        {
            status = "400 Bad Request";
            contentType = "text/html; charset=utf-8";
            responseBody = "<h1>400 Bad Request</h1><p>Missing Content-Length.</p>";
            return true;
        }

        // проверка корректности Content-Length
        if (!int.TryParse(headers["Content-Length"], out int contentLength))
        {
            status = "400 Bad Request";
            contentType = "text/html; charset=utf-8";
            responseBody = "<h1>400 Bad Request</h1><p>Invalid Content-Length.</p>";
            return true;
        }

        // ограничение размера тела запроса
        if (contentLength > MaxBodySize)
        {
            status = "413 Payload Too Large";
            contentType = "text/html; charset=utf-8";
            responseBody = "<h1>413 Payload Too Large</h1><p>Request body is too large.</p>";
            return true;
        }

        // POST-маршрут не найден
        status = "404 Not Found";
        contentType = "application/json; charset=utf-8";
        responseBody = "{\"error\":\"POST route not found\"}";
        return true;
    }

    // обработка статических файлов
    static void HandleStaticRequest(
        string path,
        string pagesPath,
        out string status,
        out string contentType,
        out string responseBody
    )
    {
        // главная страница
        if (path == "/")
        {
            status = "200 OK";
            contentType = "text/html; charset=utf-8";
            responseBody = File.ReadAllText(Path.Combine(pagesPath, "index.html"));
        }

        // страница о программе
        else if (path == "/about")
        {
            status = "200 OK";
            contentType = "text/html; charset=utf-8";
            responseBody = File.ReadAllText(Path.Combine(pagesPath, "about.html"));
        }

        // остальные файлы из pages
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
    }

    // парсинг HTTP-заголовков
    static Dictionary<string, string> ParseHeaders(string[] requestLines)
    {
        Dictionary<string, string> headers = new Dictionary<string, string>();

        for (int i = 1; i < requestLines.Length; i++)
        {
            string line = requestLines[i];

            // конец блока заголовков
            if (string.IsNullOrWhiteSpace(line))
            {
                break;
            }

            // поиск разделителя заголовка
            int colonIndex = line.IndexOf(':');

            if (colonIndex <= 0)
            {
                continue;
            }

            // извлечение имени и значения заголовка
            string headerName = line.Substring(0, colonIndex).Trim();
            string headerValue = line.Substring(colonIndex + 1).Trim();

            headers[headerName] = headerValue;
        }

        return headers;
    }

    // логирование запросов
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

        // запись всех запросов
        string accessLogLine =
            $"[{time}] {method} {path} {version} -> {status} | Host: {host} | User-Agent: {userAgent} | Accept: {accept}";

        string accessLogPath = Path.Combine(logsPath, "access.log");
        File.AppendAllText(accessLogPath, accessLogLine + Environment.NewLine);

        // отдельная запись ошибок
        if (!status.StartsWith("2"))
        {
            string errorLogLine =
                $"[{time}] ERROR {status} | {method} {path} | Host: {host} | User-Agent: {userAgent}";

            string errorLogPath = Path.Combine(logsPath, "error.log");
            File.AppendAllText(errorLogPath, errorLogLine + Environment.NewLine);
        }
    }

    // защита от path traversal
    static bool ContainsPathTraversal(string path)
    {
        string decodedPath = Uri.UnescapeDataString(path);

        return decodedPath.Contains("..") ||
               decodedPath.Contains("\\") ||
               path.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("%5c", StringComparison.OrdinalIgnoreCase);
    }

    // определение MIME-типа
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

    // отправка HTTP-ответа
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
            "Allow: GET, HEAD, OPTIONS, POST\r\n" +
            "\r\n";

        if (includeBody)
        {
            response += responseBody;
        }

        byte[] responseBytes = Encoding.UTF8.GetBytes(response);
        stream.Write(responseBytes, 0, responseBytes.Length);
    }
}
