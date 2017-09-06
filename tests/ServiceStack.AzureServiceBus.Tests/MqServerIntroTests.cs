using Funq;
using NUnit.Framework;
using ServiceStack.Auth;
using ServiceStack.Host;
using ServiceStack.Messaging;
using ServiceStack.Testing;
using ServiceStack.Text;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ServiceStack.AzureServiceBus.Tests
{
    [NonParallelizable]
    public class AzureBusServerIntroTests : MqServerIntroTests
    {
        public override IMessageService CreateMqServer(int retryCount = 1)
        {
            return new AzureBusServer(Config.AzureBusConnectionString) { RetryCount = retryCount };
        }
    }

    public class HelloIntro : IReturn<HelloIntroResponse>
    {
        public string Name { get; set; }
    }

    public class HelloFail : IReturn<HelloFailResponse>
    {
        public string Name { get; set; }
    }

    public class HelloFailResponse
    {
        public string Result { get; set; }
    }

    public class HelloIntroResponse
    {
        public string Result { get; set; }
    }

    public class HelloService : Service
    {
        public object Any(HelloIntro request)
        {
            return new HelloIntroResponse { Result = "Hello, {0}!".Fmt(request.Name) };
        }
    }

    public class MqAuthOnly : IReturn<MqAuthOnlyResponse>
    {
        public string Name { get; set; }
        public string SessionId { get; set; }
    }

    public class MqAuthOnlyResponse
    {
        public string Result { get; set; }
    }

    public class MqAuthOnlyService : Service
    {
        [Authenticate]
        public object Any(MqAuthOnly request)
        {
            var session = base.SessionAs<AuthUserSession>();
            return new MqAuthOnlyResponse
            {
                Result = "Hello, {0}! Your UserName is {1}"
                    .Fmt(request.Name, session.UserAuthName)
            };
        }
    }

    [Restrict(RequestAttributes.MessageQueue)]
    public class MqRestriction : IReturn<MqRestrictionResponse>
    {
        public string Name { get; set; }
    }

    public class MqRestrictionResponse
    {
        public string Result { get; set; }
        public ResponseStatus ResponseStatus { get; set; }
    }

    public class MqRestrictionService : Service
    {
        public object Any(MqRestriction request) =>
            new MqRestrictionResponse { Result = request.Name };
    }

    public class AppHost : AppSelfHostBase
    {
        private readonly Func<IMessageService> createMqServerFn;

        public AppHost(Func<IMessageService> createMqServerFn)
            : base("Rabbit MQ Test Host", typeof(HelloService).GetAssembly())
        {
            this.createMqServerFn = createMqServerFn;
        }

        public override void Configure(Container container)
        {
            Plugins.Add(new AuthFeature(() => new AuthUserSession(),
                new IAuthProvider[] {
                    new CredentialsAuthProvider(AppSettings),
                }));

            container.Register<IAuthRepository>(c => new InMemoryAuthRepository());
            var authRepo = container.Resolve<IAuthRepository>();

            try
            {
                ((IClearable)authRepo).Clear();
            }
            catch { /*ignore*/ }

            authRepo.CreateUserAuth(new UserAuth
            {
                Id = 1,
                UserName = "mythz",
                FirstName = "First",
                LastName = "Last",
                DisplayName = "Display",
            }, "p@55word");

            container.Register(c => createMqServerFn());

            var mqServer = container.Resolve<IMessageService>();

            mqServer.RegisterHandler<HelloIntro>(ExecuteMessage);
            mqServer.RegisterHandler<MqAuthOnly>(m =>
            {
                var req = new BasicRequest
                {
                    Verb = HttpMethods.Post,
                    Headers = { ["X-ss-id"] = m.GetBody().SessionId }
                };
                var response = ExecuteMessage(m, req);
                return response;
            });
            mqServer.RegisterHandler<MqRestriction>(ExecuteMessage);
            mqServer.Start();
        }

        protected override void Dispose(bool disposable)
        {
            var mqServer = TryResolve<IMessageService>();
            mqServer?.Dispose();

            base.Dispose(disposable);
        }
    }

    [TestFixture(Category = "Integration")]
    public abstract class MqServerIntroTests
    {
        public abstract IMessageService CreateMqServer(int retryCount = 1);

        [Test]
        public async Task Messages_with_no_responses_are_published_to_Request_outq_topic()
        {
            using (var mqServer = CreateMqServer())
            {
                await mqServer.MessageFactory.PurgeQueueAsync<HelloIntro>();
                mqServer.RegisterHandler<HelloIntro>(m =>
                {
                    "Hello, {0}!".Print(m.GetBody().Name);
                    return null;
                });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntro> msgCopy = mqClient.Get<HelloIntro>(QueueNames<HelloIntro>.Out, Config.ServerWaitTime);
                    Assert.That(msgCopy, Is.Not.Null);
                    mqClient.Ack(msgCopy);
                    Assert.That(msgCopy.GetBody().Name, Is.EqualTo("World"));
                }
            }
        }

        [Test]
        public async Task Message_with_response_are_published_to_Response_inq()
        {
            using (var mqServer = CreateMqServer())
            {
                await Task.WhenAll(
                    mqServer.MessageFactory.PurgeQueueAsync<HelloIntro>(),
                    mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<HelloIntroResponse>.In));

                mqServer.RegisterHandler<HelloIntro>(m =>
                    new HelloIntroResponse { Result = "Hello, {0}!".Fmt(m.GetBody().Name) });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
            }
        }

        [Test]
        public async Task Message_with_exceptions_are_retried_then_published_to_Request_dlq()
        {
            using (var mqServer = CreateMqServer(retryCount: 1))
            {
                await mqServer.MessageFactory.PurgeQueueAsync<HelloFail>();
                var called = 0;
                mqServer.RegisterHandler<HelloFail>(m =>
                {
                    Interlocked.Increment(ref called);
                    throw new ArgumentException("Name");
                });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloFail { Name = "World" });

                    IMessage<HelloFail> dlqMsg = mqClient.Get<HelloFail>(QueueNames<HelloFail>.Dlq, Config.ServerWaitTime);
                    mqClient.Ack(dlqMsg);

                    Assert.That(called, Is.EqualTo(2));
                    Assert.That(dlqMsg.GetBody().Name, Is.EqualTo("World"));
                    Assert.That(dlqMsg.Error?.ErrorCode, Is.EqualTo(typeof(ArgumentException).Name));
                    Assert.That(dlqMsg.Error?.Message, Is.EqualTo("Name"));
                }
            }
        }

        [Test]
        public async Task Message_with_ReplyTo_are_published_to_the_ReplyTo_queue()
        {
            using (var mqServer = CreateMqServer())
            {
                const string replyToMq = "Hello.replyto";
                await Task.WhenAll(
                   mqServer.MessageFactory.PurgeQueueAsync<HelloIntro>(),
                   mqServer.MessageFactory.PurgeQueuesAsync(replyToMq));

                mqServer.RegisterHandler<HelloIntro>(m =>
                    new HelloIntroResponse { Result = "Hello, {0}!".Fmt(m.GetBody().Name) });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    
                    mqClient.Publish(new Message<HelloIntro>(new HelloIntro { Name = "World" })
                    {
                        ReplyTo = replyToMq
                    });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(replyToMq);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
            }
        }

        [Test]
        public async Task Does_process_messages_in_HttpListener_AppHost()
        {
            var mqServer = CreateMqServer();

            await Task.WhenAll(
                mqServer.MessageFactory.PurgeQueueAsync<HelloIntro>(),
                mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<HelloIntroResponse>.In));

            using (var appHost = new AppHost(() => mqServer).Init().Start(Config.ListeningOn))
            {
                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In, Config.ServerWaitTime);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
            }
        }

        [Test]
        public async Task Does_process_multi_messages_in_HttpListener_AppHost()
        {
            var mqServer = CreateMqServer();

            await Task.WhenAll(
                mqServer.MessageFactory.PurgeQueueAsync<HelloIntro>(),
                mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<HelloIntroResponse>.In));

            using (var appHost = new AppHost(() => mqServer).Init().Start(Config.ListeningOn))
            {
                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {
                    var requests = new[]
                    {
                        new HelloIntro { Name = "Foo" },
                        new HelloIntro { Name = "Bar" },
                    };

                    var client = (IOneWayClient)mqClient;
                    client.SendAllOneWay(requests);

                    var msgResponses = new List<string>();

                    var responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In);
                    mqClient.Ack(responseMsg);
                    msgResponses.Add(responseMsg.GetBody().Result);
                    
                    

                    responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In, Config.ServerWaitTime);
                    mqClient.Ack(responseMsg);
                    msgResponses.Add(responseMsg.GetBody().Result);

                    Assert.That(msgResponses, Does.Contain("Hello, Foo!"));
                    Assert.That(msgResponses, Does.Contain("Hello, Bar!"));
                }
            }
        }

        [Test]
        public async Task Does_allow_MessageQueue_restricted_Services()
        {
            var mqServer = CreateMqServer();

            await Task.WhenAll(
                mqServer.MessageFactory.PurgeQueueAsync<MqRestriction>(),
                mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<MqRestrictionResponse>.In));

            using (var appHost = new AppHost(() => mqServer).Init().Start(Config.ListeningOn))
            {
                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {
                    mqClient.Publish(new MqRestriction
                    {
                        Name = "MQ Restriction",
                    });

                    var responseMsg = mqClient.Get<MqRestrictionResponse>(QueueNames<MqRestrictionResponse>.In, Config.ServerWaitTime);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result,
                        Is.EqualTo("MQ Restriction"));
                }
            }
        }

        [Test]
        public async Task Can_make_authenticated_requests_with_MQ()
        {
            var mqServer = CreateMqServer();

            await Task.WhenAll(
                mqServer.MessageFactory.PurgeQueueAsync<MqAuthOnly>(),
                mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<MqAuthOnlyResponse>.In));

            using (var appHost = new AppHost(() => mqServer).Init())
            {
                appHost.Start(Config.ListeningOn);

                var client = new JsonServiceClient(Config.ListeningOn);

                var response = client.Post(new Authenticate
                {
                    UserName = "mythz",
                    Password = "p@55word"
                });

                var sessionId = response.SessionId;

                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {

                    mqClient.Publish(new MqAuthOnly
                    {
                        Name = "MQ Auth",
                        SessionId = sessionId,
                    });

                    var responseMsg = mqClient.Get<MqAuthOnlyResponse>(QueueNames<MqAuthOnlyResponse>.In, Config.ServerWaitTime);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result,
                        Is.EqualTo("Hello, MQ Auth! Your UserName is mythz"));
                }
            }
        }

        [Test]
        public async Task Does_process_messages_in_BasicAppHost()
        {
            var mqServer = CreateMqServer();

            await Task.WhenAll(
                mqServer.MessageFactory.PurgeQueueAsync<HelloIntro>(),
                mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<HelloIntroResponse>.In));

            using (var appHost = new BasicAppHost(typeof(HelloService).GetAssembly())
            {
                ConfigureAppHost = host =>
                {
                    host.Container.Register(c => mqServer);
                    mqServer.RegisterHandler<HelloIntro>(host.ExecuteMessage);
                    mqServer.Start();
                }
            }.Init())
            {
                using (var mqClient = appHost.Resolve<IMessageService>().CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In, Config.ServerWaitTime);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, World!"));
                }
                appHost.Resolve<IMessageService>().Dispose();
            }
        }

        [Test]
        public async Task Global_request_and_response_get_called()
        {
            using (var mqServer = CreateMqServer())
            {
                await Task.WhenAll(
                    mqServer.MessageFactory.PurgeQueueAsync<HelloIntro>(),
                    mqServer.MessageFactory.PurgeQueuesAsync(QueueNames<HelloIntroResponse>.In));

                var azureServer = mqServer as AzureBusServer;
                azureServer.RequestFilter = msg =>
                {
                    msg.Meta["prefix"] = "Global";
                    return msg;
                };

                azureServer.ResponseFilter = response =>
                {
                    if (response is HelloIntroResponse helloResp)
                    {
                        helloResp.Result += "!!!";
                    }

                    return response;
                };

                mqServer.RegisterHandler<HelloIntro>(m =>
                    new HelloIntroResponse { Result = $"Hello, {m.Meta["prefix"]} {m.GetBody().Name}".Fmt() });
                mqServer.Start();

                using (var mqClient = mqServer.CreateMessageQueueClient())
                {
                    mqClient.Publish(new HelloIntro { Name = "World" });

                    IMessage<HelloIntroResponse> responseMsg = mqClient.Get<HelloIntroResponse>(QueueNames<HelloIntroResponse>.In);
                    mqClient.Ack(responseMsg);
                    Assert.That(responseMsg.GetBody().Result, Is.EqualTo("Hello, Global World!!!"));
                }
            }
        }
    }
}
