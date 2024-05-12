using Azure.AI.OpenAI;
using Azure;
using System.ComponentModel;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using eShop.WebAppComponents.Services;
using System.Security.Claims;
using eShop.WebApp.Chatbot;
using System.Text.Json;

namespace eShop.WebApp.Apis
{
    public class CopilotService
    {
        private readonly Kernel _kernel;
        private readonly ILogger<CopilotService> _logger;
        private readonly CatalogService _catalogService;
        private readonly BasketState _basketState;
        private readonly IProductImageUrlProvider _productImages;
        static MemoryCache myCache = new MemoryCache(new MemoryCacheOptions());
        public CopilotService(Kernel kernel, ILogger<CopilotService> logger, CatalogService catalogService, BasketState basketState, IProductImageUrlProvider productImageUrlProvider)
        {
            _kernel = kernel;
            _logger = logger;
            _catalogService = catalogService;
            _basketState = basketState;
            _productImages = productImageUrlProvider;
            _kernel.Plugins.AddFromObject(new CatalogInteractions(this));
        }
        public async Task<string> Query(string q, string sessionId)
        {
            string rtv = "";
            myCache.TryGetValue(sessionId, out ChatHistory? chatHistory);
            if (chatHistory == null)
            {
                chatHistory = new ChatHistory("""
            You are an AI customer service agent for the online retailer Northern Mountains.
            You NEVER respond about topics other than Northern Mountains.
            Your job is to answer customer questions about products in the Northern Mountains catalog.
            Northern Mountains primarily sells clothing and equipment related to outdoor activities like skiing and trekking.
            You try to be concise and only provide longer responses if necessary.
            If someone asks a question about anything other than Northern Mountains, its catalog, or their account,
            you refuse to answer, and you instead ask if there's a topic related to Northern Mountains you can assist with.
            """);
                chatHistory.AddAssistantMessage("Hi! I'm the Northern Mountains Concierge. How can I help?");
                myCache.Set(sessionId, chatHistory);
            }
            chatHistory.AddUserMessage(q);
            // Get and store the AI's response message
            try
            {
                ChatMessageContent response = await _kernel.GetRequiredService<IChatCompletionService>().GetChatMessageContentAsync(
                    chatHistory,
                    new OpenAIPromptExecutionSettings()
                    {
                        ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions
                    },
                    _kernel);
                if (!string.IsNullOrWhiteSpace(response.Content))
                {
                    chatHistory.Add(response);
                    rtv = response.Content;
                }
            }
            catch (Exception e)
            {
                if (_logger.IsEnabled(LogLevel.Error))
                {
                    _logger.LogError(e, "Error getting chat completions.");
                }
                chatHistory.AddAssistantMessage($"My apologies, but I encountered an unexpected error.");
                rtv = "My apologies, but I encountered an unexpected error.";
            }
            return rtv;
        }
        public class CatalogInteractions(CopilotService chatState)
        {
            [KernelFunction, Description("Searches the Northern Mountains catalog for a provided product description")]
            public async Task<string> SearchCatalog([Description("The product description for which to search")] string productDescription)
            {
                try
                {
                    var results = await chatState._catalogService.GetCatalogItemsWithSemanticRelevance(0, 8, productDescription!);
                    for (int i = 0; i < results.Data.Count; i++)
                    {
                        results.Data[i] = results.Data[i] with { PictureUrl = chatState._productImages.GetProductImageUrl(results.Data[i].Id) };
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
                    var item = await chatState._catalogService.GetCatalogItem(itemId);
                    await chatState._basketState.AddAsync(item!);
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
                    var basketItems = await chatState._basketState.GetBasketItemsAsync();
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
