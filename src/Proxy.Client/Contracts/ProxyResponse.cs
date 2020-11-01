﻿using System.Collections.Generic;
using System.Net;

namespace Proxy.Client.Contracts
{
    public class ProxyResponse
    {
        public HttpStatusCode StatusCode { get; internal set; }
        public IDictionary<string, string> ResponseHeaders { get; internal set; }
        public string Content { get; internal set; }
        public Timings Timings { get; internal set; }
    }
}