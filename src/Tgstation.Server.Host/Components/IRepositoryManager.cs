﻿using Microsoft.Extensions.Hosting;
using System.Threading;
using System.Threading.Tasks;

namespace Tgstation.Server.Host.Components
{
	/// <summary>
	/// Factory for creating and loading <see cref="IRepository"/>s
	/// </summary>
    interface IRepositoryManager : IHostedService
    {
		/// <summary>
		/// Attempt to load the <see cref="IRepository"/> from the default location
		/// </summary>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation</param>
		/// <returns>The loaded <see cref="IRepository"/></returns>
		Task<IRepository> LoadRepository(CancellationToken cancellationToken);

		/// <summary>
		/// Delete the current <see cref="IRepository"/> and replaces it with a clone of the repository at <paramref name="url"/>
		/// </summary>
		/// <param name="url">The location of the remote repository to clone</param>
		/// <param name="accessString">The access string to clone from <paramref name="url"/></param>
		/// <param name="cancellationToken">The <see cref="CancellationToken"/> for the operation</param>
		/// <returns>The newly cloned <see cref="IRepository"/></returns>
		Task<IRepository> CloneRepository(string url, string accessString, CancellationToken cancellationToken);

		/// <summary>
		/// Change the interval in minutes at which the repository auto updates
		/// </summary>
		/// <param name="newInterval">The new interval in minutes or <see langword="null"/> to disable the auto update</param>
		/// <returns>A <see cref="Task"/> representing the running operation</returns>
		Task SetAutoUpdateInterval(int? newInterval);
    }
}
