// Licensed to Elasticsearch B.V under
// one or more agreements.
// Elasticsearch B.V licenses this file to you under the Apache 2.0 License.
// See the LICENSE file in the project root for more information

using System.Collections.Generic;
using System.Data.Entity;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Web.Mvc;
using AspNetFullFrameworkSampleApp.Bootstrap;
using AspNetFullFrameworkSampleApp.Data;
using AspNetFullFrameworkSampleApp.Models;
using Newtonsoft.Json;

namespace AspNetFullFrameworkSampleApp.Controllers
{
	/// <summary>
	/// Demonstrates database functionality
	/// </summary>
	public class DatabaseController : ControllerBase
	{
		/// <summary>
		/// List the sample data in the database
		/// </summary>
		/// <returns></returns>
		public ActionResult Index()
		{
			using var context = new SampleDataDbContext();
			var samples = context.Set<SampleData>().ToList();
			return View(samples);
		}

		/// <summary>
		/// Allows sample data to be inserted into the database
		/// </summary>
		public ActionResult Create() => View(new CreateSampleDataViewModel());

		[HttpPost]
		[ValidateAntiForgeryToken]
		public async Task<ActionResult> Create(CreateSampleDataViewModel model)
		{
			if (!ModelState.IsValid)
				return View(model);

			int changes;
			using (var context = new SampleDataDbContext())
			{
				var sampleData = new SampleData { Name = model.Name };
				context.Set<SampleData>().Add(sampleData);
				changes = await context.SaveChangesAsync();
			}

			AddAlert(new SuccessAlert("Sample added", $"{changes} sample was saved to the database"));
			return RedirectToAction("Index");
		}

		// TODO: make this a web api controller action
		/// <summary>
		/// Generates the given count of sample data, and calls the bulk action to insert into the database
		/// </summary>
		/// <remarks>
		/// This intentionally makes an async HTTP call to bulk insert.
		/// </remarks>
		/// <param name="count">The count of sample data to generate</param>
		[HttpPost]
		public async Task<ActionResult> Generate(int count)
		{
			if (!ModelState.IsValid)
				return JsonBadRequest(new { success = false, message = "Invalid samples" });

			if (count <= 0)
				return JsonBadRequest(new { success = false, message = "count must be greater than 0" });

			int existingCount;
			using (var context = new SampleDataDbContext())
				existingCount = await context.Set<SampleData>().CountAsync();

			var samples = Enumerable.Range(existingCount, count)
				.Select(i => new CreateSampleDataViewModel { Name = $"Generated sample {i}" });

			var client = new HttpClient();
			var bulkUrl = Url.Action("Bulk", "Database", null, Request.Url.Scheme);
			var json = JsonConvert.SerializeObject(samples);
			var contentType = "application/json";
			var content = new StringContent(json, Encoding.UTF8, contentType);

			var response = await client.PostAsync(bulkUrl, content).ConfigureAwait(false);
			var responseStream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);

			return Stream(responseStream, contentType, (int)response.StatusCode);
		}

		// TODO: make this a web api controller action
		/// <summary>
		/// Bulk inserts sample data into the database
		/// </summary>
		/// <param name="model">The sample data to insert</param>
		[HttpPost]
		public async Task<ActionResult> Bulk(IEnumerable<CreateSampleDataViewModel> model)
		{
			if (!ModelState.IsValid)
				return JsonBadRequest(new { success = false, message = "Invalid samples" });

			var sampleData = model.Select(m => new SampleData { Name = m.Name });
			int changes;

			using (var context = new SampleDataDbContext())
			using (var transaction = context.Database.BeginTransaction())
			{
				context.Configuration.AutoDetectChangesEnabled = false;
				context.Configuration.ValidateOnSaveEnabled = false;
				context.Set<SampleData>().AddRange(sampleData);
				changes = await context.SaveChangesAsync();
				transaction.Commit();
			}

			return Json(new { success = true, changes });
		}
	}
}
