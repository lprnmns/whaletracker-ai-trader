using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace WhaleTracker.API.Hubs;

[Authorize]
public class MissionControlHub : Hub
{
}
