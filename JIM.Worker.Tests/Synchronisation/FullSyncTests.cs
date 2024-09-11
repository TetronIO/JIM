using JIM.Application;
using JIM.Models.Core;
using JIM.Models.Staging;
using JIM.PostgresData;
using Microsoft.EntityFrameworkCore;
using MockQueryable.Moq;
using Moq;

namespace JIM.Worker.Tests.Synchronisation;

public class FullSyncTests
{
    #region accessors
    private MetaverseObject InitiatedBy { get; set; }
    private Mock<JimDbContext> MockJimDbContext { get; set; }
    private List<ConnectedSystem> ConnectedSystemsData { get; set; } 
    private Mock<DbSet<ConnectedSystem>> MockDbSetConnectedSystems { get; set; }
    private List<ConnectedSystemObjectType> ConnectedSystemObjectTypesData { get; set; } 
    private Mock<DbSet<ConnectedSystemObjectType>> MockDbSetConnectedSystemObjectTypes { get; set; }
    private JimApplication Jim { get; set; }
    #endregion
    
    [SetUp]
    public void Setup()
    {
        // common dependencies
        TestUtilities.SetEnvironmentVariables();
        InitiatedBy = TestUtilities.GetInitiatedBy();
        
        // set up the connected systems mock
        ConnectedSystemsData = TestUtilities.GetConnectedSystemData();
        MockDbSetConnectedSystems = ConnectedSystemsData.AsQueryable().BuildMockDbSet();
        
        // todo: not sure if we need this. remove if not
        // set up the connected system object types mock. this acts as the persisted schema in JIM
        ConnectedSystemObjectTypesData = TestUtilities.GetConnectedSystemObjectTypeData();
        MockDbSetConnectedSystemObjectTypes = ConnectedSystemObjectTypesData.AsQueryable().BuildMockDbSet();
        
        // set up the metaverse object types mock
        
        // set up the sync rule stub mocks. they will be customised to specific use-cases in individual tests.
        
        // mock entity framework calls to use our data sources above
        MockJimDbContext = new Mock<JimDbContext>();
        MockJimDbContext.Setup(m => m.ConnectedSystems).Returns(MockDbSetConnectedSystems.Object);
        MockJimDbContext.Setup(m => m.ConnectedSystemObjectTypes).Returns(MockDbSetConnectedSystemObjectTypes.Object);
        
        // instantiate Jim using the mocked db context
        Jim = new JimApplication(new PostgresDataRepository(MockJimDbContext.Object));
    }
    
    [Test]
    public async Task PendingExportTestAsync()
    {
        // set up the Connected System Objects mock. this is specific to this test
        
        // mock up a connector that will return testable data
        
        // now execute Jim functionality we want to test...
        
        // confirm the results persisted to the mocked db context

        // validate the first user (who is a manager)

        // validate the second user (who is a direct-report)
        
        // validate second user manager reference

        Assert.Pass();
    }
    
    // todo: Pending Export reconciliation
    // todo: CSO obsolete process
    // todo: CSO joins to MVO
    // todo: CSO projects to MV
    // todo: MVO has pending attribute value adds for all data types as expected
    // todo: MVO has pending attribute value removes for all data types as expected
    // todo: MVO changes are persisted as expected
    // todo: DELETIONS????
    // todo: Onward updates as a result of all above scenarios
}