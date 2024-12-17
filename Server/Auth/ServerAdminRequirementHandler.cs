﻿#nullable enable

using Microsoft.AspNetCore.Authorization;
using Remotely.Server.Services;

namespace Remotely.Server.Auth;

public class ServerAdminRequirementHandler : AuthorizationHandler<ServerAdminRequirement>
{
    private readonly IDataService _dataService;

    public ServerAdminRequirementHandler(IDataService dataService)
    {
        _dataService = dataService;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ServerAdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true ||
            string.IsNullOrWhiteSpace(context.User.Identity.Name))
        {
            context.Fail();
            return;
        }

        var userResult = await _dataService.GetUserByName(context.User.Identity.Name);

        if (!userResult.IsSuccess ||
            !userResult.Value.IsServerAdmin)
        {
            context.Fail();
            return;
        }

        context.Succeed(requirement);
    }
}
