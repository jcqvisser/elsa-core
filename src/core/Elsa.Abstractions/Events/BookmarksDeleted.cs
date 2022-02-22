using System.Collections.Generic;
using Elsa.Abstractions.Multitenancy;
using Elsa.Models;
using MediatR;

namespace Elsa.Events;

public record BookmarksDeleted(string WorkflowInstanceId, IReadOnlyCollection<Bookmark> Bookmarks, Tenant Tenant) : INotification;