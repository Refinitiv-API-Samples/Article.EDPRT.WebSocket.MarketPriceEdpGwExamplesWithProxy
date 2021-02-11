﻿//|-----------------------------------------------------------------------------
//|            This source code is provided under the Apache 2.0 license      --
//|  and is provided AS IS with no warranty or guarantee of fit for purpose.  --
//|                See the project's LICENSE.md for details.                  --
//|            Copyright (C) 2018-2020 Refinitiv. All rights reserved.        --
//|-----------------------------------------------------------------------------

using System;
using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/*
 * This example demonstrates authenticating via Refinitiv Data Platform, using an
 * authentication token and a Refinitiv Real-Time service endpoint to retrieve 
 * market content.
 *
 * This example maintains a session by proactively renewing the authentication
 * token before expiration.
 *
 * It performs the following steps:
 * - Authenticating via HTTP Post request to Refinitiv Data Platform
 * - Opening a WebSocket to a specified Refinitiv Real-Time Service endpoint (host/port)
 * - Sending Login into the Real-Time Service using the token retrieved
 *   from Refinitiv Data Platform.
 * - Requesting market-price content.
 * - Printing the response content.
 * - Periodically proactively re-authenticating to Refinitiv Data Platform, and
 *   providing the updated token to the Real-Time endpoint before token expiration.
 */


namespace MarketPriceEdpGwAuthenticationExample
{
    static class Policy
    {
        public const int passwordLengthMask = 0x1;
        public const int passwordUppercaseLetterMask = 0x2;
        public const int passwordLowercaseLetterMask = 0x4;
        public const int passwordDigitMask = 0x8;
        public const int passwordSpecialCharacterMask = 0x10;
        public const int passwordInvalidCharacterMask = 0x20;

        // Default password policy
        public const int passwordLengthMin = 30;
        public const int passwordUppercaseLetterMin = 1;
        public const int passwordLowercaseLetterMin = 1;
        public const int passwordDigitMin = 1;
        public const int passwordSpecialCharacterMin = 1;
        public const String passwordSpecialCharacterSet = "~!@#$%^&*()-_=+[]{}|;:,.<>/?";
        public const int passwordMinNumberOfCategories = 3;
    }

    class MarketPriceEdpGwAuthenticationExample
    {
        /// <summary>The websocket used for retrieving market content.</summary>
        private ClientWebSocket _webSocket;

        /// <summary>Indicates whether we have successfully logged in.</summary>
        private bool _loggedIn = false;

        /// <summary>The tokens retrieved from the authentication server.
        private string _authToken;
        private string _refreshToken;

        /// <summary>The configured hostname of the Websocket server.</summary>
        private string _hostName;

        /// <summary>The configured port used when opening the WebSocket.</summary>
        private string _port = "443";

        /// <summary>The full URL of the authentication server. If not specified,
        /// https://api.refinitiv.com:443/auth/oauth2/v1/token is used.</summary>
        private string _authUrl = "https://api.refinitiv.com:443/auth/oauth2/v1/token";

        /// <summary>The configured username used when requesting the token.</summary>
        private string _userName;

        /// <summary>The configured client ID used when requesting the token.</summary>
        private string _clientId;

        /// <summary>The configured password used when requesting the token.</summary>
        private string _password;

        /// <summary>New password provided by user to change.</summary>
        private string _newPassword;

        /// <summary>The configured ApplicationID used when requesting the token.</summary>
        private string _appId = "256";

        /// <summary>The configured scope used when requesting the token.</summary>
        private string _scope = "trapi";

        /// <summary>The configured RIC used when requesting price data.</summary>
        private string _ric = "/TRI.N";

        private string _proxyHost = "127.0.0.1";
        private string _proxyPort = "8888";

        private WebProxy proxyObject;

        /// <summary>The requested service name or service ID.</summary>
        private string _service = "ELEKTRON_DD";

        /// <summary>The IP address, used as the application's position when requesting the token.</summary>
        private string _position;

        /// <summary>Amount of time until the authentication token expires; re-authenticate before then</summary>
        private int _expirationInMilliSeconds = Timeout.Infinite;

        /// <summary>Expiration time returned by password (ho refresh) request</summary>
        private int _originalExpirationInMilliSeconds = Timeout.Infinite;

        /// <summary> Specifies buffer size for each read from WebSocket.</summary>
        private static readonly int BUFFER_SIZE = 8192;

        /// <summary> This is used to cancel operations when something goes wrong. </summary>
        private CancellationTokenSource _cts = new CancellationTokenSource();

        static void Main(string[] args)
        {
            MarketPriceEdpGwAuthenticationExample example = new MarketPriceEdpGwAuthenticationExample();
            example.ParseCommandLine(args);
            example.Run();
        }

        /// <summary> Send an HTTP request to the specified authentication server, containing our username and password.
        /// The token will be used to login on the websocket. </summary>
        /// <returns><c>true</c> if success otherwise <c>false</c></returns>
        public bool GetAuthenticationInfo(bool isRefresh, string url=null)
        {
            if (string.IsNullOrEmpty(url))
                url = _authUrl;

            Console.WriteLine("Sending authentication request (isRefresh {0}) to {1}", isRefresh, url);
            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(url);
            if (proxyObject != null)
                webRequest.Proxy = proxyObject;
            try
            {
                /* Send username and password in request. */
                string postString = "username=" + _userName + "&client_id=" + _clientId;
                if (isRefresh)
                    postString += "&grant_type=refresh_token&refresh_token=" + _refreshToken;
                else
                {
                    postString += "&takeExclusiveSignOnControl=True";
                    postString += "&scope=" + _scope + "&grant_type=password&password=" + Uri.EscapeDataString(_password);
                }

                byte[] postContent = Encoding.ASCII.GetBytes(postString);
                webRequest.Method = "POST";
                webRequest.ContentType = "application/x-www-form-urlencoded";
                webRequest.ContentLength = postContent.Length;
                webRequest.AllowAutoRedirect = false;

                System.IO.Stream requestStream = webRequest.GetRequestStream();
                requestStream.Write(postContent, 0, postContent.Length);
                requestStream.Close();

                HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse();

                if (webResponse.GetResponseHeader("Transfer-Encoding").Equals("chunked") || webResponse.ContentLength > 0)
                {
                    /* If there is content in the response, print it. */
                    /* Format the object string for easier reading. */
                    dynamic msg = JsonConvert.DeserializeObject(new System.IO.StreamReader(webResponse.GetResponseStream()).ReadToEnd());
                    Console.WriteLine("RECEIVED:\n{0}\n", JsonConvert.SerializeObject(msg, Formatting.Indented));

                    // other possible items: auth_token, refresh_token, expires_in
                    _authToken = msg["access_token"].ToString();
                    _refreshToken = msg["refresh_token"].ToString();
                    if (Int32.TryParse(msg["expires_in"].ToString(), out _expirationInMilliSeconds))
                        _expirationInMilliSeconds *= 1000;
                    if (!isRefresh)
                        _originalExpirationInMilliSeconds = _expirationInMilliSeconds;
                }

                webResponse.Close();
                return true;

            }
            catch (WebException e)
            {
                HttpWebResponse response = null;
                if (e.Status == WebExceptionStatus.ProtocolError)
                {
                    response = (HttpWebResponse)e.Response;

                    HttpStatusCode statusCode = response.StatusCode;

                    bool ret = false;

                    switch (statusCode)
                    {
                        case HttpStatusCode.Moved:             // 301
                        case HttpStatusCode.Redirect:          // 302
                        case HttpStatusCode.TemporaryRedirect: // 307
                        case (HttpStatusCode)308:              // 308 Permanent Redirect
                            // Perform URL redirect
                            Console.WriteLine("Refinitiv Data Platform authentication HTTP code: {0} {1}\n", statusCode, response.StatusDescription);
                            string newHost = response.Headers.Get("Location");
                            if (!string.IsNullOrEmpty(newHost))
                                ret = GetAuthenticationInfo(isRefresh, newHost);
                            break;
                        case HttpStatusCode.BadRequest:        // 400
                        case HttpStatusCode.Unauthorized:      // 401
                            // Retry with username and password
                            Console.WriteLine("Refinitiv Data Platform authentication HTTP code: {0} {1}\n", statusCode, response.StatusDescription);
                            if (isRefresh)
                            {
                                Console.WriteLine("Retry with username and password");
                                ret = GetAuthenticationInfo(false);
                            }
                            else
                                ret = false;
                            break;
                        case HttpStatusCode.Forbidden:         // 403
                        case (HttpStatusCode)451:              // 451 Unavailable For Legal Reasons
                            // Stop retrying with the request
                            Console.WriteLine("Refinitiv Data Platform authentication HTTP code: {0} {1}\n", statusCode, response.StatusDescription);
                            Console.WriteLine("Stop retrying with the request");
                            ret = false;
                            break;
                        default:
                            // Retry the request to the Refinitiv Data Platform 
                            Console.WriteLine("Refinitiv Data Platform authentication HTTP code: {0} {1}\n", statusCode, response.StatusDescription);
                            Console.WriteLine("Retry the request to the Refinitiv Data Platform");
                            ret = GetAuthenticationInfo(isRefresh);
                            break;
                    }
                    response.Close();
                    return ret;
                }
                else
                {
                    /* The request to the authentication server failed, e.g. due to connection failure or HTTP error response. */
                    if (e.InnerException != null)
                        Console.WriteLine("Authentication server request failed: {0} -- {1}\n", e.Message, e.InnerException.Message);
                    else
                        Console.WriteLine("Authentication server request failed: {0}", e.Message);
                }
            }
            return false;
        }

        /// <summary>Runs the application. Retrives a token from the authentication server, then opens
        /// the WebSocket using the token.</summary>
        public void Run()
        {
            /* Get local hostname. */
            IPAddress hostEntry = Array.Find(Dns.GetHostEntry(Dns.GetHostName()).AddressList, ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork);
            _position = (hostEntry == null) ? "127.0.0.1/net" : hostEntry.ToString();

            /* Open a websocket. */
            Uri uri = new Uri("wss://" + _hostName + ":" + _port + "/WebSocket");
            Console.WriteLine("Connecting to WebSocket " + uri.AbsoluteUri + " ...");

            if (!GetAuthenticationInfo(false))
                Environment.Exit(1);

            _webSocket = new ClientWebSocket();
            _webSocket.Options.SetBuffer(BUFFER_SIZE, BUFFER_SIZE);
            _webSocket.Options.AddSubProtocol("tr_json2");
            if (proxyObject != null)
                _webSocket.Options.Proxy = proxyObject;
            Console.CancelKeyPress += Console_CancelKeyPress;

            try
            {
                _webSocket.ConnectAsync(uri, CancellationToken.None).Wait();

                if (_webSocket.State == WebSocketState.Open)
                {
                    SendLogin(false);

                    /* Run a take to read messages */
                    Task.Factory.StartNew(() =>
                    {
                        while (_webSocket.State == WebSocketState.Open)
                        {
                            try
                            {
                                ReceiveMessage();
                            }
                            catch (System.AggregateException)
                            {
                                System.Console.WriteLine("The WebSocket connection is closed");
                                Console_CancelKeyPress(null, null);
                            }
                        }
                    });

                    while (true)
                    {
                        Thread.Sleep((int)(_expirationInMilliSeconds * .90));
                        if (_loggedIn)
                        {
                            if (!GetAuthenticationInfo(true))
                                Environment.Exit(1);
                            if (_expirationInMilliSeconds != _originalExpirationInMilliSeconds)
                            {
                                System.Console.WriteLine("expire time changed from " + _originalExpirationInMilliSeconds / 1000
                                    + " sec to " + _expirationInMilliSeconds / 1000 + " sec; retry with password");
                                if (!GetAuthenticationInfo(false))
                                    Environment.Exit(1);
                            }
                            SendLogin(true);
                        }

                        if (_webSocket.State == WebSocketState.Aborted)
                        {
                            System.Console.WriteLine("The WebSocket connection is closed");
                            Console_CancelKeyPress(null, null);
                            break;
                        }
                    }
                }
                else
                {
                    System.Console.WriteLine("Failed to open a WebSocket connection");
                }
            }
            catch (Exception ex)
            {
                System.Console.WriteLine(ex);
            }
            finally
            {
                Console_CancelKeyPress(this, null);
            }
        }

        /// <summary>
        /// Handles Ctrl + C or exits the application.
        /// </summary>
        /// <param name="sender">The caller of this method</param>
        /// <param name="e">The <c>ConsoleCancelEventArgs</c> if any</param>
        private void Console_CancelKeyPress(object sender, ConsoleCancelEventArgs e)
        {
           if (_webSocket != null)
           {
                if (_webSocket.State == WebSocketState.Open)
                {
                    Console.WriteLine("The WebSocket connection is closed");
                    _cts.Cancel();
                    _webSocket.Dispose();
                }
           }
           Environment.Exit(0);
        }

        /// <summary>Reads data from the WebSocket and parses to JSON message</summary>
        private void ReceiveMessage()
        {
            var readBuffer = new ArraySegment<byte>(new byte[BUFFER_SIZE]);
            MemoryStream memoryStream = null;
            byte[] dataBuffer;

            while (true)
            {
                var result = _webSocket.ReceiveAsync(readBuffer, _cts.Token);

                if (result.IsFaulted)
                {
                    Console.WriteLine("Read message failed " + result.Exception.Message);
                    Console_CancelKeyPress(this, null);
                }
                else
                {
                    if (!result.Result.EndOfMessage)
                    {
                        if (memoryStream == null) memoryStream = new MemoryStream(BUFFER_SIZE * 5);

                        memoryStream.Write(readBuffer.Array, readBuffer.Offset, readBuffer.Count);
                        readBuffer = new ArraySegment<byte>(new byte[BUFFER_SIZE]);
                    }
                    else
                    {
                        if (memoryStream != null)
                        {
                            memoryStream.Write(readBuffer.Array, readBuffer.Offset, readBuffer.Count);
                            dataBuffer = memoryStream.GetBuffer();
                            memoryStream.Dispose();
                        }
                        else
                        {
                            dataBuffer = readBuffer.Array;
                        }

                        break;
                    }
                }
            };

            /* Received message(s). */
            JArray messages = JArray.Parse(Encoding.ASCII.GetString(dataBuffer));
            /* Print the message (format the object string for easier reading). */
            Console.WriteLine("RECEIVED:\n{0}\n", JsonConvert.SerializeObject(messages, Formatting.Indented));

            for (int index = 0; index < messages.Count; ++index)
                ProcessJsonMsg(messages[index]);
        }

        /// <summary>
        /// Creates and sends a login message
        /// </summary>
        /// <param name="isRefresh">Setting <c>true</c> to not interest in the login refresh</param>
        private void SendLogin(bool isRefresh)
        {
            string msg;
            msg = "{" + "\"ID\":1," + "\"Domain\":\"Login\"," + "\"Key\": {\"NameType\":\"AuthnToken\"," +
                "\"Elements\":{\"ApplicationId\":\"" + _appId + "\"," + "\"Position\":\"" + _position + "\"," +
                "\"AuthenticationToken\":\"" + _authToken + "\"}}";
            if (isRefresh)
                msg += ",\"Refresh\": false";
            msg += "}";
            SendMessage(msg);
        }

        /// <summary>
        /// Processes the received message. If the message is a login response indicating we are now logged in,
        /// opens a stream for price content.
        /// </summary>
        /// <param name="msg">The received JSON message</param>
        void ProcessJsonMsg(dynamic msg)
        {
            switch ((string)msg["Type"])
            {
                case "Refresh":
                    if ((string)msg["Domain"] == "Login")
                    {
                        if (msg["State"] != null && (string)msg["State"]["Stream"] != "Open")
                        {
                            Console.WriteLine("Login stream was closed.\n");
                            Environment.Exit(1);
                        }

                        if (!_loggedIn && (msg["State"] == null || (string)msg["State"]["Data"] == "Ok"))
                        {
                            /* Login was successful. */
                            _loggedIn = true;

                            /* Request an item. */
                            SendMessage("{" + "\"ID\": 2," + "\"Key\": {\"Name\":\"" + _ric + "\",\"Service\":\"" + _service + "\"}" + "}");
                        }
                    }
                    break;
                case "Status":
                    if (msg["Domain"] != null && (string)msg["Domain"] == "Login" &&
                        msg["State"] != null && msg["State"]["Stream"] != null && (string)msg["State"]["Stream"] != "Open")
                    {
                        Console.WriteLine("Stream is no longer open (state is {0})", (string)msg["State"]["Stream"]);
                        Environment.Exit(1);
                    }
                    break;
                case "Ping":
                    SendMessage("{\"Type\":\"Pong\"}");
                    break;
                default:
                    break;
            }
        }

        /// <summary>Prints the outbound message and sends it on the WebSocket.</summary>
        /// <param name="jsonMsg">Message to send</param>
        void SendMessage(string jsonMsg)
        {
            /* Print the message (format the object string for easier reading). */
            Console.WriteLine("SENT:\n{0}\n", JsonConvert.SerializeObject(JsonConvert.DeserializeObject(jsonMsg), Formatting.Indented));

            var encoded = Encoding.ASCII.GetBytes(jsonMsg);
            var buffer = new ArraySegment<Byte>(encoded, 0, encoded.Length);
            _webSocket.SendAsync(buffer, WebSocketMessageType.Text, true, _cts.Token).Wait();
        }

        /// <summary>Parses command-line arguments.</summary>
        /// <param name="args">Command-line arguments passed to the application.</param>
        void ParseCommandLine(string[] args)
        {
            for (int i = 0; i < args.Length; ++i)
            {
                // all commands require an argument
                if (i + 1 >= args.Length)
                {
                    Console.WriteLine("{0} requires an argument.", args[i]);
                    PrintCommandLineUsageAndExit();
                }
                switch (args[i])
                {
                    case "--app_id":
                        _appId = args[++i];
                        break;
                    case "--auth_url":
                        _authUrl = args[++i];
                        break;
                    case "--hostname":
                        _hostName = args[++i];
                        break;
                    case "--password":
                        _password = args[++i];
                        break;
                    case "--newPassword":
                        _newPassword = args[++i];
                        break;
                    case "--port":
                        _port = args[++i];
                        break;
                    case "--ric":
                        _ric = args[++i];
                        break;
                    case "--scope":
                        _scope = args[++i];
                        break;
                    case "--user":
                        _userName = args[++i];
                        break;
                    case "--clientid":
                        _clientId = args[++i];
                        break;
                    case "--service":
                        _service = args[++i];
                        break;
                    case "--proxy_hostname":
                        _proxyHost = args[++i];
                        break;
                    case "--proxy_port":
                        _proxyPort = args[++i];
                        break;
                    default:
                        Console.WriteLine("Unknown option: {0}", args[i]);
                        PrintCommandLineUsageAndExit();
                        break;
                }
            }

            int _proxyPortInt;

            if (!string.IsNullOrEmpty(_proxyHost) && int.TryParse(_proxyPort, out _proxyPortInt))
            {
                proxyObject = new WebProxy(_proxyHost, _proxyPortInt);
            }
            else
            {
                proxyObject = null;
                Console.WriteLine($"--proxy_port is not a number, not using proxy");
            }

            if (_userName == null || _password == null || _clientId == null)
            {
                Console.WriteLine("User, password and clientid must be specified on the command line");
                PrintCommandLineUsageAndExit();
            }

            if (_hostName == null)
            {
                Console.WriteLine("hostname must be specified on the command line");
                PrintCommandLineUsageAndExit();
            }

            if (!(_newPassword == null))
            {
                int result = checkNewPassword(_newPassword);

                if ((result & Policy.passwordInvalidCharacterMask) != 0)
                {
                    Console.WriteLine("New password contains invalid symbol\n" +
                        "valid symbols are [A-Z][a-z][0-9]" + Policy.passwordSpecialCharacterSet);
                    Environment.Exit(1);
                }

                if ((result & Policy.passwordLengthMask) != 0)
                {
                    Console.WriteLine("New password length should be at least " 
                        + Policy.passwordLengthMin + " characters");
                    Environment.Exit(1);
                }

                int countCategories = 0;
                for (int mask = Policy.passwordUppercaseLetterMask; 
                    mask <= Policy.passwordSpecialCharacterMask; mask <<= 1)
                {
                    if ((result & mask) == 0)
                    {
                        countCategories++;
                    }
                }
                if (countCategories < Policy.passwordMinNumberOfCategories)
                {
                    Console.WriteLine("Password must contain characters belonging to at least "
                        + Policy.passwordMinNumberOfCategories 
                        + " of the following four categories:\n"
                        + "uppercase letters, lowercase letters, digits, and special characters.\n");
                    Environment.Exit(1);
                }

                if (ChangePassword())
                {
                    Console.WriteLine("Password successfully changed");
                    _password = _newPassword;
                    _newPassword = null;
                }
                else
                {
                    Environment.Exit(1);
                }
            }
        }

        /// <summary>Prints usage information. Used when arguments cannot be parsed.</summary>
        void PrintCommandLineUsageAndExit()
        {
            Console.WriteLine("Usage: {0} [--app_id appId] [--auth_url url] [--auth_port port] [--hostname hostname] [--password password] [--newPassword new_password] [--port port] [--ric ric] [--scope scope] [--user user] [--clientid clientID] [--service service] [--proxy_hostname proxy_hostname] [--proxy_port proxy_port]", System.AppDomain.CurrentDomain.FriendlyName);
            Environment.Exit(1);
        }

        /// <summary>Recognises  characteristics of proposed new password.</summary>
        /// <returns>set of bits describing the results of the check.</returns>
        public static int checkNewPassword(string pwd)
        {
            int result = 0;

            if (pwd.Length < Policy.passwordLengthMin)
            {
                result |= Policy.passwordLengthMask;
            }

            int countUpper = 0;
            int countLower = 0;
            int countDigit = 0;
            int countSpecial = 0;

            for (int i = 0; i < pwd.Length; i++)
            {
                char c = pwd[i];
                if (!Regex.IsMatch(new string(c, 1), "[A-Za-z0-9]") 
                    && !Policy.passwordSpecialCharacterSet.Contains(c))
                {
                    result |= Policy.passwordInvalidCharacterMask;
                }
                if (Char.IsUpper(c))
                {
                    countUpper++;
                }
                if (Char.IsLower(c))
                {
                    countLower++;
                }
                if (Char.IsDigit(c))
                {
                    countDigit++;
                }

                if (Policy.passwordSpecialCharacterSet.Contains(c))
                {
                    countSpecial++;
                }
            }

            if (countUpper < Policy.passwordUppercaseLetterMin)
            {
                result |= Policy.passwordUppercaseLetterMask;
            }
            if (countLower < Policy.passwordLowercaseLetterMin)
            {
                result |= Policy.passwordLowercaseLetterMask;
            }
            if (countDigit < Policy.passwordDigitMin)
            {
                result |= Policy.passwordDigitMask;
            }
            if (countSpecial < Policy.passwordSpecialCharacterMin)
            {
                result |= Policy.passwordSpecialCharacterMask;
            }

            return result;
        }

        /// <summary> Send change password request to the  authentication server.</summary>
        /// <returns><c>true</c> if success otherwise <c>false</c></returns>
        public bool ChangePassword()
        {
            Console.WriteLine("Sending change password request to " + _authUrl);
            HttpWebRequest webRequest = (HttpWebRequest)WebRequest.Create(_authUrl);
            if (proxyObject != null)
                webRequest.Proxy = proxyObject;
            try
            {
                string postString = "username=" + _userName + "&client_id=" + _clientId;
                postString += "&takeExclusiveSignOnControl=True";
                postString += "&scope=" + _scope + "&grant_type=password&password=" + Uri.EscapeDataString(_password);
                postString += "&newPassword=" + Uri.EscapeDataString(_newPassword);

                byte[] postContent = Encoding.ASCII.GetBytes(postString);
                webRequest.Method = "POST";
                webRequest.ContentType = "application/x-www-form-urlencoded";
                webRequest.ContentLength = postContent.Length;
                webRequest.AllowAutoRedirect = false;

                System.IO.Stream requestStream = webRequest.GetRequestStream();
                requestStream.Write(postContent, 0, postContent.Length);
                requestStream.Close();

                HttpWebResponse webResponse = (HttpWebResponse)webRequest.GetResponse();

                webResponse.Close();
                return true;

            }
            catch (WebException e)
            {
                HttpWebResponse response = null;
                if (e.Status == WebExceptionStatus.ProtocolError)
                {
                    response = (HttpWebResponse)e.Response;

                    HttpStatusCode statusCode = response.StatusCode;

                    bool ret = false;

                    switch (statusCode)
                    {
                        case HttpStatusCode.Moved:             // 301
                        case HttpStatusCode.Redirect:          // 302
                        case HttpStatusCode.TemporaryRedirect: // 307
                        case (HttpStatusCode)308:              // 308 Permanent Redirect
                            // Perform URL redirect
                            Console.WriteLine("Request to aurh server is redirected");
                            string newHost = response.Headers.Get("Location");
                            if (!string.IsNullOrEmpty(newHost))
                                ret = ChangePassword();
                            break;
                        case HttpStatusCode.BadRequest:        // 400
                        case HttpStatusCode.Unauthorized:      // 401
                        case HttpStatusCode.Forbidden:         // 403
                        case (HttpStatusCode)451:              // 451 Unavailable For Legal Reasons
                            // Error of changing password
                            Console.WriteLine("Change password error");
                            if (response.ContentLength > 0)
                            {
                                /* If there is content in the response, print it. */
                                dynamic msg = JsonConvert.DeserializeObject(new System.IO.StreamReader(response.GetResponseStream()).ReadToEnd());
                                Console.WriteLine("RECEIVED:\n{0}\n", JsonConvert.SerializeObject(msg, Formatting.Indented));
                            }
                            ret = false;
                            break;
                        default:
                            // Retry the request to the API gateway
                            Console.WriteLine("Error changing password. Receive HTTP code: {0} {1}\n", statusCode, response.StatusDescription);
                            Console.WriteLine("Retry the request to the API gateway");
                            ret = ChangePassword();
                            break;
                    }
                    response.Close();
                    return ret;
                }
                else
                {
                    /* The request to the authentication server failed, e.g. due to connection failure or HTTP error response. */
                    if (e.InnerException != null)
                        Console.WriteLine("Authentication server request failed: {0} -- {1}\n", e.Message, e.InnerException.Message);
                    else
                        Console.WriteLine("Authentication server request failed: {0}", e.Message);
                }
            }
            return false;
        }
    }
}
