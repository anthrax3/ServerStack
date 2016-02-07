﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Streamer;
using Microsoft.Extensions.DependencyInjection;

namespace Sample.Middleware
{
    public class ServerChannel : IDisposable
    {
        private readonly Stream _stream;
        private readonly JsonSerializer _serializer;
        private readonly Dictionary<string, Func<Request, Response>> _callbacks = new Dictionary<string, Func<Request, Response>>(StringComparer.OrdinalIgnoreCase);
        private readonly IServiceProvider _serviceProvider;

        private bool _isBound;

        public ServerChannel(Stream stream, JsonSerializerSettings settings, IServiceProvider serviceProvider)
        {
            _stream = stream;
            _serializer = JsonSerializer.Create(settings);
            _serviceProvider = serviceProvider;
        }

        public async Task StartAsync()
        {
            try
            {
                while (true)
                {
                    var reader = new JsonTextReader(new StreamReader(_stream));


                    var request = _serializer.Deserialize<Request>(reader);

                    Response response = null;

                    Func<Request, Response> callback;
                    if (_callbacks.TryGetValue(request.Method, out callback))
                    {
                        response = callback(request);
                    }
                    else
                    {
                        // If there's no method then return a failed response for this request
                        response = new Response
                        {
                            Id = request.Id,
                            Error = string.Format("Unknown method '{0}'", request.Method)
                        };
                    }

                    await Write(response);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex.Message);
            }
        }

        public IDisposable Bind<T>() where T : class
        {
            if (_isBound)
            {
                throw new NotSupportedException("Can't bind to different objects");
            }

            _isBound = true;

            var methods = new List<string>();

            foreach (var m in typeof(T).GetTypeInfo().DeclaredMethods.Where(m => m.IsPublic))
            {
                methods.Add(m.Name);

                var parameters = m.GetParameters();

                if (_callbacks.ContainsKey(m.Name))
                {
                    throw new NotSupportedException(String.Format("Duplicate definitions of {0}. Overloading is not supported.", m.Name));
                }

                _callbacks[m.Name] = request =>
                {
                    var response = new Response();
                    response.Id = request.Id;

                    T value = _serviceProvider.GetService<T>() ?? Activator.CreateInstance<T>();

                    try
                    {
                        var args = request.Args.Zip(parameters, (a, p) => a.ToObject(p.ParameterType))
                                               .ToArray();

                        var result = m.Invoke(value, args);

                        if (result != null)
                        {
                            response.Result = JToken.FromObject(result);
                        }
                    }
                    catch (TargetInvocationException ex)
                    {
                        response.Error = ex.InnerException.Message;
                    }
                    catch (Exception ex)
                    {
                        response.Error = ex.Message;
                    }

                    return response;
                };
            }

            return new DisposableAction(() =>
            {
                foreach (var m in methods)
                {
                    lock (_callbacks)
                    {
                        _callbacks.Remove(m);
                    }
                }
            });
        }

        private Task Write(object value)
        {
            var data = JsonConvert.SerializeObject(value);

            var bytes = Encoding.UTF8.GetBytes(data);

            return _stream.WriteAsync(bytes, 0, bytes.Length);
        }

        public void Dispose()
        {
            _stream.Dispose();
        }

        private class DisposableAction : IDisposable
        {
            private Action _action;

            public DisposableAction(Action action)
            {
                _action = action;
            }

            public void Dispose()
            {
                Interlocked.Exchange(ref _action, () => { }).Invoke();
            }
        }
    }
}
