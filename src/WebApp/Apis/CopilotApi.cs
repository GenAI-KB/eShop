using eShop.WebApp.Chatbot;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.SemanticKernel;
using eShop.WebAppComponents.Services;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.Extensions.DependencyInjection;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Server;

namespace eShop.WebApp.Apis
{
    public static class CopilotApi
    {
        public static BasketState? basketState;
        static MemoryCache myCache = new MemoryCache(new MemoryCacheOptions());
        public static RouteGroupBuilder MapCopilotApiV1(this IEndpointRouteBuilder app)
        {
            var api = app.MapGroup("api/copilot");

            var kernel = app.ServiceProvider.GetService<Kernel>()!;

            kernel.Plugins.AddFromObject(new CatalogInteractions(app));
            api.MapGet("/", async ([FromQuery] string q, [FromQuery] string sessionId) =>
            {
                var msg = await Query(q, sessionId, kernel);
                return TypedResults.Ok(msg);
            });
            return api;
        }
        public static async Task<string> Query(string q, string sessionId, Kernel kernel)
        {
            string rtv = "";
            myCache.TryGetValue(sessionId, out ChatHistory? chatHistory);
            if (chatHistory == null)
            {
                chatHistory = new ChatHistory("""
            # for answer customer's question
            You are an AI customer service agent for the online retailer Northern Mountains.
            You NEVER respond about topics other than Northern Mountains.
            Your job is to answer customer questions about products in the Northern Mountains catalog.
            Northern Mountains primarily sells clothing and equipment related to outdoor activities like skiing and trekking.
            You try to be concise and only provide longer responses if necessary.
            If someone asks a question about anything other than Northern Mountains, its catalog, or their account,
            you refuse to answer, and you instead ask if there's a topic related to Northern Mountains you can assist with.
            
            # for system process
            After answering the customer, you should append the id information about the customer's question at the end of your answer.
            If customer ask for a brand: {"question":{"brandId":"Brand Id"}}
            if customer ask for a type: {"question":{"typeId":"Type Id"}}
            if customer ask for a product: {"question":{"productId":"Product Id"}}
            if customer ask for  change basket: {"question":{"basket":1}}
            if customer ask for bran and type:{"question":{"brandId":"Brand Id","typeId":"Type Id"}}
            """);
                chatHistory.AddAssistantMessage("Hi! I'm the Northern Mountains Concierge. How can I help?");
                myCache.Set(sessionId, chatHistory);
            }
            chatHistory.AddUserMessage(q);
            // Get and store the AI's response message
            try
            {
                ChatMessageContent response = await kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(
                    chatHistory,
                    new OpenAIPromptExecutionSettings()
                    {
                        ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
                    },
                    kernel);
                if (!string.IsNullOrWhiteSpace(response.Content))
                {
                    chatHistory.Add(response);
                    rtv = response.Content;
                }
            }
            catch (Exception)
            {
                chatHistory.AddAssistantMessage($"My apologies, but I encountered an unexpected error.");
                rtv = "My apologies, but I encountered an unexpected error.";
            }
            return rtv;
        }

        public class CatalogInteractions(IEndpointRouteBuilder app)
        {
            private BasketState GetBasketState(IEndpointRouteBuilder app)
            {
                BasketService basketService = app.ServiceProvider.GetRequiredService<BasketService>();
                CatalogService catalogService = app.ServiceProvider.GetRequiredService<CatalogService>();
                OrderingService orderingService = app.ServiceProvider.GetRequiredService<OrderingService>();
                var authenticationStateProvider = new ServerAuthenticationStateProvider();
                BasketState basketState = new BasketState(basketService, catalogService, orderingService, authenticationStateProvider);
                return basketState;
            }
            [KernelFunction, Description("Searches the Northern Mountains catalog for a provided product description")]
            public async Task<string> SearchCatalog([Description("The product description for which to search")] string productDescription)
            {
                try
                {
                    var catalogService = app.ServiceProvider.GetRequiredService<CatalogService>();
                    var productImages = app.ServiceProvider.GetRequiredService<IProductImageUrlProvider>();
                    var results = await catalogService.GetCatalogItemsWithSemanticRelevance(0, 8, productDescription!);
                    for (int i = 0; i < results.Data.Count; i++)
                    {
                        results.Data[i] = results.Data[i] with { PictureUrl = productImages.GetProductImageUrl(results.Data[i].Id) };
                    }

                    return JsonSerializer.Serialize(results);
                }
                catch (HttpRequestException e)
                {
                    return Error(e, "Error accessing catalog.");
                }
            }

            [KernelFunction, Description("Adds a product to the user's shopping cart.")]
            public async Task<string> AddToCart([Description("The id of the product to add to the shopping cart (basket)")] int itemId)
            {
                try
                {
                    var catalogService = app.ServiceProvider.GetRequiredService<CatalogService>();
                    var item = await catalogService.GetCatalogItem(itemId);
                    await GetBasketState(app).AddAsync(item!);
                    return "Item added to shopping cart.";
                }
                catch (Grpc.Core.RpcException e) when (e.StatusCode == Grpc.Core.StatusCode.Unauthenticated)
                {
                    return "Unable to add an item to the cart. You must be logged in.";
                }
                catch (Exception e)
                {
                    return Error(e, "Unable to add the item to the cart.");
                }
            }

            [KernelFunction, Description("Gets information about the contents of the user's shopping cart (basket)")]
            public async Task<string> GetCartContents()
            {
                try
                {
                    var basketItems = await GetBasketState(app).GetBasketItemsAsync();
                    return JsonSerializer.Serialize(basketItems);
                }
                catch (Exception e)
                {
                    return Error(e, "Unable to get the cart's contents.");
                }
            }

            private string Error(Exception e, string message)
            {
                return message;
            }
        }
    }
}
